using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DirPorter.Core.Diagnostics;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Privilege;

namespace DirPorter.Core.Copy;

/// <summary>
/// 复制引擎 B：自研，基于 <c>CopyFileExW</c>。
///
/// 与引擎 A（robocopy）的取舍分工（R0 设计 §10 的 B 位落地）：
/// <list type="table">
/// <item><term>并发模型</term><description>文件级并行（N 个工作线程各复制一个文件），
/// 单文件内部走 <c>CopyFileEx</c> 的进度回调 → **同时拿到多线程吞吐与字节级进度**。</description></item>
/// <item><term>进度</term><description>字节级（含多流累计），远超 robocopy 的文件级粒度。</description></item>
/// <item><term>暂停/继续</term><description>真正的暂停：在进度回调里阻塞，不杀进程、不丢已复制数据。</description></item>
/// <item><term>元数据</term><description>由本引擎自己搬运：ADS/时间戳/属性/稀疏/安全描述符。
/// 其中安全描述符的能力**随权限降级**（见 <see cref="SecurityInfoOps"/>），与 robocopy 的降级口径一致。</description></item>
/// </list>
///
/// 铁律（与引擎 A 完全一致）：**跳过重解析点**，遇目录内的链接一律不进入，
/// 否则会递归进目标数据甚至成环。
/// </summary>
public sealed class CopyFileExCopier : IFileCopier, IPausableCopier
{
    private readonly IPrivilegeProbe _privileges;
    private readonly SecurityCopyCapability _security;

    public CopyFileExCopier(IPrivilegeProbe? privileges = null, SecurityCopyCapability? security = null)
    {
        _privileges = privileges ?? Win32PrivilegeProbe.Current;
        _security = security ?? SecurityInfoOps.Current;
    }

    public string Name => "native";

    public PauseGate Pause { get; } = new();

    public CopyCapabilities Capabilities => new(
        PreservesAcl: true,
        PreservesOwnerAndAuditing: _security.CanCopyOwnerAndGroup && _security.CanCopyAuditing,
        PreservesTimestamps: true,
        PreservesAlternateDataStreams: true,
        PreservesSparse: true,
        HandlesEncryptedFiles: false,
        SkipsReparsePoints: true,
        SupportsPauseResume: true,
        ProgressGranularity: "字节级（CopyFileEx 进度回调，多线程聚合）");

    /// <summary>本引擎将采用的安全信息策略（供 Dry-run / 日志如实展示）。</summary>
    public SecurityCopyCapability SecurityCapability => _security;

    public async Task<CopyResult> CopyAsync(
        CopyRequest request, IProgress<CopyProgress>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var source = PathNormalizer.TrimTrailingSeparator(PathNormalizer.NormalizeAbsolute(request.SourcePath));
        var target = PathNormalizer.TrimTrailingSeparator(PathNormalizer.NormalizeAbsolute(request.TargetPath));
        var sourcePlain = PathNormalizer.StripExtendedPrefix(source);
        var targetPlain = PathNormalizer.StripExtendedPrefix(target);

        // ---------- 1) 扫描源（跳过重解析点，与 /XJ 同语义）----------
        var scan = Scan(source);
        var failures = new ConcurrentBag<CopyFailure>();
        foreach (var error in scan.Errors) failures.Add(error);

        var context = new CopyContext(request.Options, progress, sourcePlain, targetPlain);
        var canceled = false;

        try
        {
            // ---------- 2) 创建目录（浅 → 深）----------
            foreach (var relative in scan.Directories.OrderBy(static d => Depth(d)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var directory = relative.Length == 0 ? target : PathNormalizer.Combine(target, relative);
                if (ReparsePointOps.Inspect(directory).PathExists) continue;

                if (!Kernel32.CreateDirectory(directory, IntPtr.Zero))
                {
                    var code = Win32Error.Last;
                    if (code != Kernel32.ErrorAlreadyExists)
                    {
                        failures.Add(new CopyFailure(PathNormalizer.StripExtendedPrefix(directory),
                            $"创建目录失败：{Win32Error.Describe(code)}", code));
                    }
                }
            }

            // ---------- 3) 并行复制文件 ----------
            var threads = Math.Clamp(request.Options.Threads, 1, 128);

            await Parallel.ForEachAsync(
                scan.Files,
                new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancellationToken },
                (file, token) =>
                {
                    CopyOneFileWithRetry(source, target, file, context, failures, token);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 约定：取消不是异常，而是"返回值里如实说明"（引擎据此走回滚）。
            canceled = true;
        }

        // ---------- 4) 目录元数据（深 → 浅，避免父目录时间戳被后续写入顶掉）----------
        if (!canceled)
        {
            foreach (var relative in scan.Directories.OrderByDescending(static d => Depth(d)))
            {
                var sourceDirectory = relative.Length == 0 ? source : PathNormalizer.Combine(source, relative);
                var targetDirectory = relative.Length == 0 ? target : PathNormalizer.Combine(target, relative);

                ApplyDirectoryMetadata(sourceDirectory, targetDirectory, request.Options, context);
            }
        }

        context.Flush();

        var failureList = failures.ToList();
        var outcome = canceled
            ? CopyOutcome.Canceled
            : failureList.Count == 0 ? CopyOutcome.Success
            : context.FilesCopied > 0 ? CopyOutcome.Partial
            : CopyOutcome.Failed;

        var securityNote = request.Options.CopySecurity ? _security.DegradedReason : "安全信息复制已关闭（未搬运 DACL/所有者/审计）。";

        return new CopyResult(
            Outcome: outcome,
            ExitCode: canceled ? -1 : failureList.Count == 0 ? 0 : 1,
            FilesCopied: context.FilesCopied,
            FilesSkipped: context.FilesSkipped,
            BytesCopied: context.BytesCopied,
            FailedCount: failureList.Count,
            Failures: failureList,
            RawLogPath: null,
            Elapsed: stopwatch.Elapsed)
        {
            SecurityNote = securityNote,
        };
    }

    // ==================================================================
    //  单文件复制
    // ==================================================================

    private void CopyOneFileWithRetry(
        string sourceRoot, string targetRoot, SourceFile file,
        CopyContext context, ConcurrentBag<CopyFailure> failures, CancellationToken cancellationToken)
    {
        // 暂停闸门：在文件之间也要能停住。
        if (!Pause.WaitWhilePaused(() => cancellationToken.IsCancellationRequested))
            return;

        var sourcePath = PathNormalizer.Combine(sourceRoot, file.RelativePath);
        var targetPath = PathNormalizer.Combine(targetRoot, file.RelativePath);

        // 增量续做：目标已存在且大小与最后写入时间都一致 → 跳过。
        // 这正是"暂停后继续"能接着做的依据（已完成的文件不会重传）。
        if (IsAlreadyCopied(sourcePath, targetPath, file.Size))
        {
            context.MarkSkipped(file.Size);
            return;
        }

        var options = context.Options;

        // EFS：默认拒绝，绝不静默解密。
        if ((file.Attributes & Kernel32.FileAttributeEncrypted) != 0 && !options.AllowDecryptEncryptedFiles)
        {
            failures.Add(new CopyFailure(PathNormalizer.StripExtendedPrefix(sourcePath),
                "EFS 加密文件：默认不解密复制（静默解密属于安全降级）。如需解密复制请显式开启对应选项。", null));
            return;
        }

        var attempts = Math.Max(1, options.RetryCount + 1);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                CopyOneFileOnce(sourcePath, targetPath, file, options, context, cancellationToken);
                context.MarkCopied();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;

                // 失败重试前先清掉半截目标文件，避免下次 CopyFileEx 在残片上继续。
                TryDeletePartial(targetPath);

                if (attempt < attempts)
                {
                    var wait = options.ExponentialBackoff
                        ? options.RetryWaitSeconds * (1 << (attempt - 1))
                        : options.RetryWaitSeconds;

                    Thread.Sleep(TimeSpan.FromSeconds(Math.Clamp(wait, 0, 60)));
                }
            }
        }

        failures.Add(new CopyFailure(
            PathNormalizer.StripExtendedPrefix(sourcePath),
            $"复制失败（已重试 {attempts - 1} 次）：{lastError?.Message}", Win32ErrorCodeOf(lastError)));
    }

    private void CopyOneFileOnce(
        string sourcePath, string targetPath, SourceFile file,
        CopyOptions options, CopyContext context, CancellationToken cancellationToken)
    {
        var cancelState = new CancelState();
        using var registration = cancellationToken.Register(static state => Volatile.Write(ref ((CancelState)state!).Value, 1), cancelState);

        var lastTransferred = 0L;
        var fileName = Path.GetFileName(PathNormalizer.StripExtendedPrefix(sourcePath));

        var routine = new Kernel32.CopyProgressRoutine((total, transferred, _, _, _, _, _, _, _) =>
        {
            if (cancelState.Value != 0 || cancellationToken.IsCancellationRequested) return Kernel32.ProgressCancel;
            if (!Pause.WaitWhilePaused(() => cancelState.Value != 0 || cancellationToken.IsCancellationRequested))
                return Kernel32.ProgressCancel;

            // totalBytesTransferred 是**跨流累计**的（ADS 也算），因此用差分累计到全局。
            var delta = transferred - lastTransferred;
            lastTransferred = transferred;

            if (delta > 0) context.AddBytes(delta);
            context.ReportThrottled(fileName);

            return Kernel32.ProgressContinue;
        });

        var flags = 0u;
        if (options.NoBuffering) flags |= Kernel32.CopyFileNoBuffering;
        if (options.AllowDecryptEncryptedFiles) flags |= Kernel32.CopyFileAllowDecryptedDestination;

        if (!Kernel32.CopyFileEx(sourcePath, targetPath, routine, IntPtr.Zero, ref cancelState.Value, flags))
        {
            var code = Win32Error.Last;
            if (code == Kernel32.ErrorAccessDenied && (file.Attributes & Kernel32.FileAttributeEncrypted) != 0)
                throw new IOException($"EFS 加密文件无法复制（{Win32Error.Describe(code)}）：需要目标卷支持加密或显式允许解密。");

            throw new IOException($"{Win32Error.Describe(code)}（源：{PathNormalizer.StripExtendedPrefix(sourcePath)}）");
        }

        ApplyFileMetadata(sourcePath, targetPath, file, options, context);
    }

    // ==================================================================
    //  元数据
    // ==================================================================

    private void ApplyFileMetadata(string sourcePath, string targetPath, SourceFile file, CopyOptions options, CopyContext context)
    {
        // 1) 稀疏优化：仅当源带稀疏属性。内部自带"确认全零才置零"的安全阀。
        if (options.ReplicateSparse && (file.Attributes & Kernel32.FileAttributeSparseFile) != 0)
        {
            var sparse = SparseFileOps.Replicate(sourcePath, targetPath, file.Size, CancellationToken.None);
            if (sparse.Note is not null) context.AddNote($"稀疏文件 {Path.GetFileName(PathNormalizer.StripExtendedPrefix(targetPath))}：{sparse.Note}");
        }

        // 2) 时间戳（创建/访问/写入）
        SetTimestamps(targetPath, file, isDirectory: false, context);

        // 3) 属性（只写可设置的位；稀疏/压缩/加密不在 SetFileAttributes 的能力范围内）
        var settable = file.Attributes & Kernel32.SettableFileAttributes;
        if (settable != 0 && !Kernel32.SetFileAttributes(targetPath, settable))
        {
            var code = Win32Error.Last;
            context.AddNote($"设置文件属性失败 {PathNormalizer.StripExtendedPrefix(targetPath)}：{Win32Error.Describe(code)}");
        }

        // 4) 安全描述符（最后做：设置了限制性 ACL 之后再想改属性就难了）
        if (options.CopySecurity)
        {
            var outcome = SecurityInfoOps.Copy(sourcePath, targetPath, _security);
            if (outcome.DegradedReason is not null) context.AddNote(outcome.DegradedReason);
        }
    }

    private void ApplyDirectoryMetadata(
        string sourceDirectory, string targetDirectory, CopyOptions options, CopyContext context)
    {
        if (!ReparsePointOps.Inspect(targetDirectory).PathExists) return;

        // 目录的自身时间戳/属性要从源目录读（枚举时只拿到子项信息）。
        var info = FileInformation.Query(sourceDirectory);
        if (info is null)
        {
            context.AddNote($"无法读取源目录元数据，跳过：{PathNormalizer.StripExtendedPrefix(sourceDirectory)}");
            return;
        }

        var value = info.Value;
        var entry = new SourceFile(
            string.Empty, 0, value.dwFileAttributes,
            ToTicks(value.ftCreationTime), ToTicks(value.ftLastAccessTime), ToTicks(value.ftLastWriteTime));

        SetTimestamps(targetDirectory, entry, isDirectory: true, context);

        var settable = value.dwFileAttributes & Kernel32.SettableFileAttributes;
        if (settable != 0 && !Kernel32.SetFileAttributes(targetDirectory, settable))
        {
            var code = Win32Error.Last;
            context.AddNote($"设置目录属性失败 {PathNormalizer.StripExtendedPrefix(targetDirectory)}：{Win32Error.Describe(code)}");
        }

        if (options.CopySecurity)
        {
            var outcome = SecurityInfoOps.Copy(sourceDirectory, targetDirectory, _security);
            if (outcome.DegradedReason is not null) context.AddNote(outcome.DegradedReason);
        }
    }

    private void SetTimestamps(string path, SourceFile entry, bool isDirectory, CopyContext context)
    {
        var access = Kernel32.FileWriteAttributes;
        var handle = Kernel32.CreateFile(
            path, access,
            Kernel32.FileShareRead | Kernel32.FileShareWrite | Kernel32.FileShareDelete,
            IntPtr.Zero, Kernel32.OpenExisting,
            isDirectory ? Kernel32.FileFlagBackupSemantics : 0,
            IntPtr.Zero);

        if (handle == Kernel32.InvalidHandleValue)
        {
            context.AddNote($"打开以写时间戳失败 {PathNormalizer.StripExtendedPrefix(path)}：{Win32Error.Describe(Win32Error.Last)}");
            return;
        }

        try
        {
            var creation = Kernel32.FileTime.FromTicks(entry.CreationTicks);
            var lastAccess = Kernel32.FileTime.FromTicks(entry.LastAccessTicks);
            var lastWrite = Kernel32.FileTime.FromTicks(entry.LastWriteTicks);

            var buffers = new IntPtr[3];
            try
            {
                buffers[0] = Marshal.AllocHGlobal(Marshal.SizeOf<Kernel32.FileTime>());
                buffers[1] = Marshal.AllocHGlobal(Marshal.SizeOf<Kernel32.FileTime>());
                buffers[2] = Marshal.AllocHGlobal(Marshal.SizeOf<Kernel32.FileTime>());

                Marshal.StructureToPtr(creation, buffers[0], fDeleteOld: false);
                Marshal.StructureToPtr(lastAccess, buffers[1], fDeleteOld: false);
                Marshal.StructureToPtr(lastWrite, buffers[2], fDeleteOld: false);

                if (!Kernel32.SetFileTime(handle, buffers[0], buffers[1], buffers[2]))
                    context.AddNote($"设置时间戳失败 {PathNormalizer.StripExtendedPrefix(path)}：{Win32Error.Describe(Win32Error.Last)}");
            }
            finally
            {
                foreach (var buffer in buffers)
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    // ==================================================================
    //  扫描
    // ==================================================================

    private static ScanResult Scan(string sourceExtended)
    {
        var directories = new List<string>();
        var files = new List<SourceFile>();
        var errors = new List<CopyFailure>();

        var sourcePlain = PathNormalizer.StripExtendedPrefix(sourceExtended);
        var stack = new Stack<string>();
        stack.Push(sourceExtended);

        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            var relativeDirectory = Path.GetRelativePath(sourcePlain, PathNormalizer.StripExtendedPrefix(directory));
            if (relativeDirectory == ".") relativeDirectory = string.Empty;

            directories.Add(relativeDirectory);

            var searchPath = PathNormalizer.EnsureTrailingSeparator(directory) + "*";
            var handle = Kernel32.FindFirstFileEx(
                searchPath, Kernel32.FindExInfoBasic, out var data,
                Kernel32.FindExSearchNameMatch, IntPtr.Zero, Kernel32.FindFirstExLargeFetch);

            if (handle == Kernel32.InvalidHandleValue)
            {
                var code = Win32Error.Last;
                if (code is not (Kernel32.ErrorFileNotFound or Kernel32.ErrorPathNotFound))
                {
                    errors.Add(new CopyFailure(PathNormalizer.StripExtendedPrefix(directory),
                        $"枚举目录失败：{Win32Error.Describe(code)}", code));
                }

                continue;
            }

            try
            {
                while (true)
                {
                    var name = Kernel32.GetFileName(data);
                    if (name is not ("." or ".."))
                    {
                        var child = PathNormalizer.Combine(directory, name);
                        var attributes = data.dwFileAttributes;
                        var isDirectory = (attributes & Kernel32.FileAttributeDirectory) != 0;
                        var isReparse = (attributes & Kernel32.FileAttributeReparsePoint) != 0;

                        // ⚠️ 铁律：绝不进入重解析点（与 robocopy /XJ 同语义）。
                        if (!isReparse)
                        {
                            var relative = string.IsNullOrEmpty(relativeDirectory) ? name : Path.Combine(relativeDirectory, name);

                            if (isDirectory)
                            {
                                stack.Push(child);
                            }
                            else
                            {
                                files.Add(new SourceFile(relative, Kernel32.ToFileSize(data), attributes,
                                    ToTicks(data.ftCreationTime), ToTicks(data.ftLastAccessTime), ToTicks(data.ftLastWriteTime)));
                            }
                        }
                    }

                    if (!Kernel32.FindNextFile(handle, out data)) break;
                }
            }
            finally
            {
                Kernel32.FindClose(handle);
            }
        }

        return new ScanResult(directories, files, errors);
    }

    // ==================================================================
    //  辅助
    // ==================================================================

    private static bool IsAlreadyCopied(string sourcePath, string targetPath, long expectedSize)
    {
        var targetInfo = FileInformation.Query(targetPath);
        if (targetInfo is null) return false;

        var info = targetInfo.Value;
        var targetSize = ((long)info.nFileSizeHigh << 32) | info.nFileSizeLow;
        if (targetSize != expectedSize) return false;

        var sourceInfo = FileInformation.Query(sourcePath);
        if (sourceInfo is null) return false;

        return ToTicks(sourceInfo.Value.ftLastWriteTime) == ToTicks(info.ftLastWriteTime);
    }

    private static long ToTicks(Kernel32.FileTime time) => ((long)time.dwHighDateTime << 32) | time.dwLowDateTime;

    private static int Depth(string relativePath)
        => relativePath.Length == 0 ? 0 : relativePath.Count(static c => c == '\\') + 1;

    private static void TryDeletePartial(string targetPath)
    {
        try
        {
            if (File.Exists(targetPath))
            {
                Kernel32.SetFileAttributes(targetPath, Kernel32.FileAttributeNormal);
                File.Delete(targetPath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DirPorter] 清理半截目标文件失败：{ex.Message}");
        }
    }

    private static int? Win32ErrorCodeOf(Exception? exception)
        => exception is DirPorterNativeException native ? native.Win32Code : (int?)null;

    // ==================================================================
    //  内部类型
    // ==================================================================

    /// <summary>CopyFileEx 的取消标志容器（必须是稳定的字段地址，故用类而不是局部变量）。</summary>
    private sealed class CancelState
    {
        public int Value;
    }

    private readonly record struct SourceFile(
        string RelativePath, long Size, uint Attributes,
        long CreationTicks, long LastAccessTicks, long LastWriteTicks);

    private sealed record ScanResult(
        List<string> Directories,
        List<SourceFile> Files,
        List<CopyFailure> Errors);

    /// <summary>复制过程中的全局计数与进度节流（多线程安全）。</summary>
    private sealed class CopyContext
    {
        private const int ReportIntervalMs = 100;   // ≤10 次/秒（非功能要求）

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly object _reportLock = new();
        private readonly ConcurrentDictionary<string, byte> _notes = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _noteOrder = new();
        private const int MaxNotes = 200;

        private long _bytesDone;
        private long _filesDone;
        private long _filesSkipped;
        private long _lastReportMs;

        public CopyContext(CopyOptions options, IProgress<CopyProgress>? progress, string sourceRoot, string targetRoot)
        {
            Options = options;
            ProgressCallback = progress;
            SourceRoot = sourceRoot;
            TargetRoot = targetRoot;
        }

        public CopyOptions Options { get; }
        public IProgress<CopyProgress>? ProgressCallback { get; }
        public string SourceRoot { get; }
        public string TargetRoot { get; }

        public long BytesCopied => Interlocked.Read(ref _bytesDone);
        public long FilesCopied => Interlocked.Read(ref _filesDone);
        public long FilesSkipped => Interlocked.Read(ref _filesSkipped);
        public IReadOnlyCollection<string> Notes => _noteOrder.ToArray();

        public void AddBytes(long delta) => Interlocked.Add(ref _bytesDone, delta);

        public void MarkCopied() => Interlocked.Increment(ref _filesDone);

        public void MarkSkipped(long size)
        {
            Interlocked.Increment(ref _filesSkipped);
            Interlocked.Add(ref _bytesDone, size);
        }

        /// <summary>
        /// 记录一条降级/异常说明。
        /// **必须去重**：像"仅复制 DACL"这类说明会命中每个文件，
        /// 不去重就会产生几万条相同文本，把报告变成噪声。
        /// </summary>
        public void AddNote(string note)
        {
            if (!_notes.TryAdd(note, 0)) return;

            _noteOrder.Enqueue(note);
            while (_noteOrder.Count > MaxNotes) _noteOrder.TryDequeue(out _);
        }

        public void ReportThrottled(string? currentItem)
        {
            if (ProgressCallback is null) return;

            var elapsedMs = _stopwatch.ElapsedMilliseconds;

            lock (_reportLock)
            {
                if (elapsedMs - _lastReportMs < ReportIntervalMs) return;
                _lastReportMs = elapsedMs;
            }

            Report(currentItem);
        }

        public void Flush() => Report(null);

        private void Report(string? currentItem)
        {
            if (ProgressCallback is null) return;

            var elapsed = _stopwatch.Elapsed.TotalSeconds;
            var done = Interlocked.Read(ref _bytesDone);
            var speed = elapsed > 0 ? done / elapsed : 0;
            var remaining = Options.SourceTotalBytes - done;
            var eta = speed > 0 && remaining > 0 ? TimeSpan.FromSeconds(remaining / speed) : (TimeSpan?)null;

            ProgressCallback.Report(new CopyProgress(
                Options.SourceFileCount,
                Interlocked.Read(ref _filesDone) + Interlocked.Read(ref _filesSkipped),
                Options.SourceTotalBytes,
                done,
                speed,
                eta,
                currentItem));
        }
    }
}
