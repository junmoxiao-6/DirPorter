using System.Text;
using DirPorter.Core.Diagnostics;

namespace DirPorter.Core.FileSystem;

/// <summary>一条服务 / 开机启动项引用。</summary>
public sealed record StartupReference(string Name, string Value, string Category);

/// <summary>
/// 服务与开机启动项引用探测（PRECHECK 源侧第 4 条「任何正在被服务或开机启动项引用的目录」）。
///
/// 实现要点：
/// - **只读注册表**，不写任何项；用 P/Invoke 而非 reg.exe（本机安全策略会拦截 reg.exe）。
/// - 枚举一次后缓存（进程内），逐源匹配只做字符串比较。
/// - 任何读取失败都静默降级为"未命中"，绝不阻塞预检本身。
/// </summary>
public sealed class StartupReferenceProbe
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private const string RunKey64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    private readonly Lazy<IReadOnlyList<StartupReference>> _references;

    public StartupReferenceProbe()
    {
        _references = new Lazy<IReadOnlyList<StartupReference>>(LoadAll, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>当前已加载的引用条目（供 UI 展示与测试）。</summary>
    public IReadOnlyList<StartupReference> References => _references.Value;

    /// <summary>找出引用了 <paramref name="plainSourcePath"/>（或其内部文件）的启动项 / 服务。</summary>
    public IReadOnlyList<StartupReference> FindReferences(string plainSourcePath)
    {
        if (string.IsNullOrWhiteSpace(plainSourcePath)) return Array.Empty<StartupReference>();

        string normalizedSource;
        try
        {
            normalizedSource = PathNormalizer.NormalizeAbsolute(PathNormalizer.StripExtendedPrefix(plainSourcePath));
        }
        catch (ArgumentException)
        {
            return Array.Empty<StartupReference>();
        }

        var hits = new List<StartupReference>();
        foreach (var reference in References)
        {
            var referencedPath = ExtractPath(reference.Value);
            if (referencedPath is null) continue;

            try
            {
                if (PathNormalizer.IsSameOrUnder(PathNormalizer.NormalizeAbsolute(referencedPath), normalizedSource))
                    hits.Add(reference);
            }
            catch (ArgumentException)
            {
                // 引用的路径本身不合法，跳过。
            }
        }

        return hits;
    }

    /// <summary>把注册表值还原为文件路径：去掉引号、去掉命令行参数、展开环境变量。</summary>
    public static string? ExtractPath(string registryValue)
    {
        if (string.IsNullOrWhiteSpace(registryValue)) return null;

        var text = registryValue.Trim();

        // 形如 "C:\Path With Space\app.exe" -arg
        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            if (closing > 1) text = text[1..closing];
        }
        else
        {
            // 未加引号：以 ".exe"（含自身）为界截断；没有 .exe 则取第一个空格之前的部分。
            var exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex > 0)
            {
                text = text[..(exeIndex + 4)];
            }
            else
            {
                var space = text.IndexOf(' ');
                if (space > 0) text = text[..space];
            }
        }

        text = Environment.ExpandEnvironmentVariables(text.Trim());

        // 只接受绝对路径或含 \ 的相对形态；纯命令名（如 "svchost.exe"）无法判定归属
        if (!Path.IsPathFullyQualified(text)) return null;

        return text;
    }

    private static IReadOnlyList<StartupReference> LoadAll()
    {
        var list = new List<StartupReference>();

        try
        {
            ReadServices(list);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 读取服务列表失败：{ex.Message}");
        }

        try
        {
            ReadRunValues(list, Advapi32.HkeyLocalMachine, RunKey64, "HKLM Run");
            ReadRunValues(list, Advapi32.HkeyLocalMachine, RunKey32, "HKLM Run (WOW6432)");
            ReadRunValues(list, Advapi32.HkeyCurrentUser, RunKey64, "HKCU Run");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 读取启动项失败：{ex.Message}");
        }

        return list;
    }

    private static unsafe void ReadServices(List<StartupReference> list)
    {
        var status = Advapi32.RegOpenKeyEx(Advapi32.HkeyLocalMachine, ServicesKey, 0, Advapi32.KeyRead, out var servicesKey);
        if (status != Kernel32.ErrorSuccess) return;

        try
        {
            var nameBuffer = new char[512];
            var index = 0u;

            while (true)
            {
                uint nameLength = (uint)nameBuffer.Length;
                int result;
                fixed (char* pName = nameBuffer)
                {
                    result = Advapi32.RegEnumKeyEx(servicesKey, index, pName, ref nameLength, IntPtr.Zero, null, null, IntPtr.Zero);
                }

                if (result != Kernel32.ErrorSuccess) break;

                var serviceName = new string(nameBuffer, 0, (int)nameLength);

                var imagePath = ReadStringValue(servicesKey, serviceName, "ImagePath");
                if (!string.IsNullOrWhiteSpace(imagePath))
                    list.Add(new StartupReference(serviceName, imagePath, "服务 ImagePath"));

                index++;
                if (index > 4096) break; // 防御性上限，避免异常注册表导致死循环
            }
        }
        finally
        {
            Advapi32.RegCloseKey(servicesKey);
        }
    }

    private static void ReadRunValues(List<StartupReference> list, IntPtr root, string subKey, string category)
    {
        var status = Advapi32.RegOpenKeyEx(root, subKey, 0, Advapi32.KeyRead, out var key);
        if (status != Kernel32.ErrorSuccess) return;

        try
        {
            // Run 键的值数量很少，直接穷举名称
            foreach (var valueName in EnumerateValueNames(key))
            {
                var value = ReadStringValue(key, null, valueName);
                if (!string.IsNullOrWhiteSpace(value))
                    list.Add(new StartupReference(valueName, value, category));
            }
        }
        finally
        {
            Advapi32.RegCloseKey(key);
        }
    }

    private static unsafe List<string> EnumerateValueNames(IntPtr key)
    {
        var names = new List<string>();
        var buffer = new char[1024];
        var index = 0u;

        while (index < 4096)
        {
            uint nameLength = (uint)buffer.Length;
            int result;
            fixed (char* pName = buffer)
            {
                result = Advapi32.RegEnumValue(key, index, pName, ref nameLength, IntPtr.Zero, out _, null, null);
            }

            if (result != Kernel32.ErrorSuccess) break;
            names.Add(new string(buffer, 0, (int)nameLength));
            index++;
        }

        return names;
    }

    private static unsafe string? ReadStringValue(IntPtr root, string? subKeyName, string valueName)
    {
        var key = root;

        if (subKeyName is not null)
        {
            var openStatus = Advapi32.RegOpenKeyEx(root, subKeyName, 0, Advapi32.KeyRead, out key);
            if (openStatus != Kernel32.ErrorSuccess) return null;
        }

        try
        {
            var type = 0u;
            uint size = 0;
            var queryStatus = Advapi32.RegQueryValueEx(key, valueName, IntPtr.Zero, out type, null, ref size);
            if (queryStatus != Kernel32.ErrorSuccess || size < 2) return null;
            if (type != Advapi32.RegSz && type != Advapi32.RegExpandSz) return null;
            if (size > 64 * 1024) return null;

            var bytes = new byte[size];
            fixed (byte* p = bytes)
            {
                queryStatus = Advapi32.RegQueryValueEx(key, valueName, IntPtr.Zero, out type, p, ref size);
            }

            if (queryStatus != Kernel32.ErrorSuccess) return null;

            var text = Encoding.Unicode.GetString(bytes, 0, (int)Math.Min(size, (uint)bytes.Length)).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirPorter] 读取注册表值 {subKeyName}\\{valueName} 失败：{ex.Message}");
            return null;
        }
        finally
        {
            if (subKeyName is not null) Advapi32.RegCloseKey(key);
        }
    }
}
