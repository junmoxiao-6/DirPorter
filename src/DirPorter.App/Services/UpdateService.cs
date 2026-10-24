using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DirPorter.App.Services;

/// <summary>下载进度：百分比 + 已下载/总量 + 实时速度（慢网络下"有没有在动"比百分比更重要）。</summary>
public sealed record DownloadProgress(double Percent, long Received, long Total, double BytesPerSecond);

/// <summary>发布清单里的一个版本（约定的 JSON 结构）。</summary>
public sealed record UpdateInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string? Sha256 = null,
    [property: JsonPropertyName("notes")] string? Notes = null);

/// <summary>
/// 检查并应用更新（R12.6 / R12.8）。
///
/// 清单是一个静态 JSON（可放 GitHub Releases、对象存储、任意 HTTPS 静态托管，
/// 也可以是本地文件路径）：
/// <code>
/// { "version": "0.1.10", "url": "https://…/dirporter.exe",
///   "sha256": "ab12…", "notes": "修复：列宽拖动" }
/// </code>
///
/// 地址解析优先级（最终用户什么都不用填）：
///   ① 设置页「更新清单地址」 ② 环境变量 <c>DIRPORTER_UPDATE_URL</c>
///   ③ 编译期常量（csproj 的 <c>UpdateFeedUrl</c>） ④ exe 同目录的 <c>latest.json</c>
/// </summary>
public static class UpdateService
{
    /// <summary>检查清单用：需要"快速失败"。</summary>
    private static readonly HttpClient Http = CreateClient(TimeSpan.FromSeconds(60));

    /// <summary>
    /// 下载更新包用：**不能设总超时**。
    /// HttpClient.Timeout 覆盖"从发起到读完整个 body"，而更新包约 65 MB ——
    /// 30 秒的限制会让它必然超时（0.3.2 实测：检查能过、下载必挂）。
    /// 这里改为无总超时，由调用方用 CancellationToken 控制整体上限。
    /// </summary>
    private static readonly HttpClient DownloadHttp = CreateClient(System.Threading.Timeout.InfiniteTimeSpan);

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };

        // GitHub API 强制要求 User-Agent，缺失会直接 403。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DirPorter-UpdateChecker/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        return client;
    }

    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 1, 0);

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    /// <summary>编译期写入的更新清单地址（csproj 里的 AssemblyMetadata）。</summary>
    public static string BakedFeedUrl
    {
        get
        {
            try
            {
                return typeof(UpdateService).Assembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => string.Equals(a.Key, "UpdateFeedUrl", StringComparison.OrdinalIgnoreCase))
                    ?.Value?.Trim() ?? string.Empty;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>按优先级决定"从哪拿清单"。</summary>
    public static string ResolveFeedUrl(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        var fromEnv = Environment.GetEnvironmentVariable("DIRPORTER_UPDATE_URL");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();

        if (!string.IsNullOrWhiteSpace(BakedFeedUrl)) return BakedFeedUrl;

        // 兜底：exe 同目录放一个 latest.json（离线/内网分发时最省事）。
        var local = Path.Combine(AppContext.BaseDirectory, "latest.json");
        return File.Exists(local) ? local : string.Empty;
    }

    /// <summary>解析清单并判断是否有更新；网络/格式问题抛异常由调用方提示。</summary>
    public static async Task<(bool HasUpdate, UpdateInfo? Info)> CheckAsync(string feedUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            throw new InvalidOperationException("no-feed");
        }

        // GitHub：优先读 Release 的 latest.json（固定下载地址，不经过 API，没有 60 次/小时限流）；
        // 该附件缺失/网络不通时回落 API。
        // 两条路径各自重试一次：国内访问 GitHub 偶发抽风很常见，一次失败不代表源不可用。
        if (TryParseGitHub(feedUrl, out var apiUrl, out var slug))
        {
            var manifestUrl = $"https://github.com/{slug}/releases/latest/download/latest.json";
            var failures = new List<string>();

            foreach (var channel in new[] { "Release 清单", "GitHub API" })
            {
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        return channel == "Release 清单"
                            ? await CheckManifestAsync(manifestUrl, cancellationToken).ConfigureAwait(false)
                            : await CheckGitHubAsync(apiUrl, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                    {
                        failures.Add($"{channel} 第 {attempt} 次：{DescribeNetworkError(ex)}");

                        if (attempt < 2) await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            throw new HttpRequestException(
                "无法访问更新源（已依次尝试 Release 清单与 API，各重试一次）。" +
                string.Join("；", failures.Distinct()) +
                "。请检查网络或代理后重试。");
        }

        return await CheckManifestAsync(feedUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取并校验「清单格式」（静态 JSON / Release 附件 / 本地文件）。</summary>
    private static async Task<(bool HasUpdate, UpdateInfo? Info)> CheckManifestAsync(
        string feedUrl, CancellationToken cancellationToken)
    {
        var json = IsRemote(feedUrl)
            ? await Http.GetStringAsync(feedUrl, cancellationToken).ConfigureAwait(false)
            : await File.ReadAllTextAsync(feedUrl, cancellationToken).ConfigureAwait(false);

        var info = JsonSerializer.Deserialize<UpdateInfo>(json)
                   ?? throw new InvalidDataException("清单内容为空或格式不正确。");

        if (string.IsNullOrWhiteSpace(info.Version) || string.IsNullOrWhiteSpace(info.Url))
        {
            throw new InvalidDataException("清单缺少 version 或 url 字段。");
        }

        return (IsNewer(info.Version, CurrentVersion), info);
    }

    /// <summary>纯比较：清单版本是否高于当前版本（容忍 v 前缀与 -preview 后缀）。</summary>
    public static bool IsNewer(string remoteVersion, Version current)
    {
        var text = Normalize(remoteVersion).TrimStart('v', 'V');

        return Version.TryParse(text, out var remote) && remote > current;
    }

    /// <summary>
    /// 下载更新包（R12.16）。
    ///
    /// 两个关键点，都是被实测逼出来的：
    /// <list type="number">
    /// <item><b>不设总超时</b>：更新包约 65 MB，国内直连 GitHub 实测只有 ~21 KB/s，
    ///   固定超时必然半途而废；这里只设 45 分钟上限（足够 20 KB/s 下完 65 MB）。</item>
    /// <item><b>断点续传</b>：先写 <c>.part</c>，中断后下次用 Range 从断点继续 ——
    ///   慢网络下要用户从头再来一遍是不可接受的。</item>
    /// </list>
    /// 有 sha256 时下载完强制校验；不匹配则删除临时文件并报错。
    /// </summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsRemote(info.Url))
        {
            var copy = Path.Combine(Path.GetTempPath(), $"dirporter-update-{Guid.NewGuid():N}.exe");
            File.Copy(info.Url, copy, overwrite: true);
            VerifyOrThrow(copy, info.Sha256);
            return copy;
        }

        var staging = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DirPorter", "updates");
        Directory.CreateDirectory(staging);

        var tag = Sanitize(info.Version);
        var hashTag = string.IsNullOrWhiteSpace(info.Sha256) ? "nohash" : info.Sha256!.Trim()[..Math.Min(8, info.Sha256.Trim().Length)];
        var partPath = Path.Combine(staging, $"dirporter-{tag}-{hashTag}.part");
        var exePath = Path.Combine(staging, $"dirporter-{tag}-{hashTag}.exe");

        // 之前已经下完并校验通过 → 直接复用。
        if (File.Exists(exePath) && HashMatches(exePath, info.Sha256)) return exePath;

        var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(TimeSpan.FromMinutes(45));

        using var request = new HttpRequestMessage(HttpMethod.Get, info.Url);
        if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);

        using var response = await DownloadHttp
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, overall.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // 只有服务端认了 Range（206）才能续写；返回 200 说明要从头开始。
        var append = offset > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!append) offset = 0;

        var length = response.Content.Headers.ContentLength ?? -1;
        var total = length > 0 ? length + offset : -1;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long received = offset;
        long lastReportTicks = 0;
        var lastBytes = offset;

        await using (var source = await response.Content.ReadAsStreamAsync(overall.Token).ConfigureAwait(false))
        await using (var destination = new FileStream(
                         partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int read;

            while ((read = await source.ReadAsync(buffer, overall.Token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), overall.Token).ConfigureAwait(false);
                received += read;

                // 500ms 节流：太频繁会让 UI 线程被格式化和刷新淹没。
                var now = stopwatch.ElapsedMilliseconds;
                if (now - lastReportTicks < 500 && received != total) continue;

                lastReportTicks = now;
                var seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                var speed = (received - lastBytes) / Math.Max(0.001, (now - (lastReportTicks - 500)) / 1000.0);

                progress?.Report(new DownloadProgress(
                    total > 0 ? (double)received / total * 100 : 0,
                    received,
                    total,
                    speed));

                lastBytes = received;
            }
        }

        if (total > 0 && received < total)
        {
            throw new IOException($"下载不完整（{received:N0} / {total:N0} 字节）。已保留断点，重试会继续。");
        }

        VerifyOrThrow(partPath, info.Sha256);

        File.Move(partPath, exePath, overwrite: true);
        return exePath;
    }

    private static string Sanitize(string version)
    {
        var chars = version.Where(static c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }

    private static bool HashMatches(string path, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return File.Exists(path);

        try
        {
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            return string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void VerifyOrThrow(string path, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return;

        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

        if (string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase)) return;

        try { File.Delete(path); } catch (IOException) { }

        throw new InvalidDataException(
            $"下载文件校验失败（期望 {expected.Trim()[..Math.Min(12, expected.Trim().Length)]}…，实际 {actual[..12]}…）。已删除临时文件。");
    }

    /// <summary>
    /// 退出后替换自身并重启。流程：把新 exe 暂存到 LocalAppData → 写一个等待脚本 →
    /// 脚本反复尝试覆盖（进程未退出时文件被锁，等它释放）→ 启动新版本。
    ///
    /// 返回 false 表示"无法自动替换"（安装目录不可写 / 无权限），调用方应提示手动替换。
    /// 注意：**不依赖 tasklist**（企业策略常禁用），只靠"复制失败就重试"判断进程是否已退出。
    /// </summary>
    public static bool ApplyAndRestart(string downloadedExe)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(downloadedExe)) return false;

        // ① 目标目录必须可写，否则让用户早点知道（而不是等替换时静默失败）。
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(exe)!, $".dp-write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        // ② 新版本挪到稳定位置（系统临时目录可能被清理）。
        string staged;
        try
        {
            var staging = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DirPorter", "updates");
            Directory.CreateDirectory(staging);

            staged = Path.Combine(staging, $"dirporter-{DateTime.Now:yyyyMMdd-HHmmss}.exe");
            File.Copy(downloadedExe, staged, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            staged = downloadedExe;
        }

        // ③ 把"自己"复制到临时目录，作为更新器运行：
        //    它等主进程退出 → 校验大小 → 原子替换 → 重启。
        //    不能用主进程自身直接替换（文件正被自己占用）。
        var updater = Path.Combine(Path.GetTempPath(), $"dirporter-updater-{Guid.NewGuid():N}.exe");

        try
        {
            File.Copy(exe, updater, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            var arguments = $"apply-update --new \"{staged}\" --target \"{exe}\" --pid {Environment.ProcessId} --relaunch";

            Process.Start(new ProcessStartInfo(updater, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            try { File.Delete(updater); } catch (IOException) { }
            return false;
        }
    }

    /// <summary>
    /// 识别 GitHub 形式的更新源，输出对应的 API 地址。
    /// 支持两种写法：<c>github:owner/repo</c> 与 <c>https://github.com/owner/repo</c>。
    /// </summary>
    public static bool TryParseGitHub(string feedUrl, out string apiUrl) =>
        TryParseGitHub(feedUrl, out apiUrl, out _);

    /// <summary>同上，额外给出 <c>owner/repo</c>（用于拼 Release 固定下载地址）。</summary>
    public static bool TryParseGitHub(string feedUrl, out string apiUrl, out string slug)
    {
        apiUrl = string.Empty;
        slug = string.Empty;
        if (string.IsNullOrWhiteSpace(feedUrl)) return false;

        var text = feedUrl.Trim();
        string? raw = null;

        if (text.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            raw = text["github:".Length..];
        }
        else
        {
            var marker = "github.com/";
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) raw = text[(index + marker.Length)..];
        }

        if (raw is null) return false;

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        // 去掉 .git 后缀（直接粘贴仓库地址的情况）。
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];

        slug = $"{parts[0]}/{repo}";
        apiUrl = $"https://api.github.com/repos/{slug}/releases/latest";
        return true;
    }

    /// <summary>读 GitHub「最新 Release」：版本取 tag，下载地址取 .exe 附件，摘要取 digest。</summary>
    private static async Task<(bool HasUpdate, UpdateInfo? Info)> CheckGitHubAsync(
        string apiUrl, CancellationToken cancellationToken)
    {
        var json = await Http.GetStringAsync(apiUrl, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagElement)) throw new InvalidDataException("Release 缺少 tag_name。");

        var tag = tagElement.GetString() ?? string.Empty;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("该 Release 没有任何附件。");
        }

        JsonElement? asset = null;
        foreach (var item in assets.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            asset = item;
            if (name.Equals("dirporter.exe", StringComparison.OrdinalIgnoreCase)) break;
        }

        if (asset is null) throw new InvalidDataException("该 Release 没有 .exe 附件。");

        var url = asset.Value.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidDataException("Release 附件没有下载地址。");

        string? sha = asset.Value.TryGetProperty("digest", out var d) ? d.GetString() : null;
        if (sha is not null && sha.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = sha["sha256:".Length..];

        var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

        var info = new UpdateInfo(tag, url, sha, notes);
        return (IsNewer(tag, CurrentVersion), info);
    }

    /// <summary>把网络异常翻译成用户能懂的一句话（界面上不再暴露 .NET 原始文案）。</summary>
    public static string DescribeNetworkError(Exception ex) => ex switch
    {
        TaskCanceledException => "超时（60 秒内没有响应）",
        HttpRequestException { StatusCode: not null } h => $"HTTP {(int)h.StatusCode}",
        HttpRequestException => "连接失败",
        IOException => "读取中断",
        _ => ex.GetType().Name,
    };

    private static bool IsRemote(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string version)
    {
        var text = version.Trim();
        var dash = text.IndexOfAny(new[] { '-', '+' });
        return dash > 0 ? text[..dash] : text;
    }
}
