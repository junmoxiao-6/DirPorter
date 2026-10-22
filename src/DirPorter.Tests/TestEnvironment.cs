using System.Runtime.CompilerServices;
using System.IO;
using DirPorter.Core.Config;

namespace DirPorter.Tests;

/// <summary>
/// 测试进程级环境初始化。
///
/// ⚠️ 为什么必须有它：<see cref="DirPorterPaths"/> 的数据根目录是静态懒加载的（首选
/// <c>%ProgramData%\DirPorter</c>）。单元测试里凡是走 Journal / 迁移记录 / 台账的用例，
/// 都在往**用户真实的数据目录**里写东西 —— 实测中发现 <c>%ProgramData%\DirPorter\runs</c>
/// 里堆着测试产生的 journal，<c>migrations.json</c> 也被测试追加过记录。
///
/// <see cref="ModuleInitializerAttribute"/> 保证这段代码在**任何测试运行之前**执行一次
/// （早于所有静态构造与测试方法），因此能在 <see cref="DirPorterPaths"/> 首次被访问前
/// 把数据根切到临时目录。这样测试与用户数据彻底隔离，也就敢在测试里跑真实的迁移。
/// </summary>
internal static class TestEnvironment
{
    /// <summary>测试专用的数据根目录（进程退出后保留，便于排查失败用例）。</summary>
    public static string DataRoot { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // 已经显式指定过（例如演练脚本）就不覆盖，尊重外部意图。
        var existing = Environment.GetEnvironmentVariable(DirPorterPaths.DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            DataRoot = existing;
            return;
        }

        DataRoot = Path.Combine(Path.GetTempPath(), "dirporter-tests-data");
        Directory.CreateDirectory(DataRoot);
        Environment.SetEnvironmentVariable(DirPorterPaths.DataRootEnvironmentVariable, DataRoot);
    }
}
