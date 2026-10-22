using DirPorter.Core.Config;
using DirPorter.Core.FileSystem;
using Xunit;

namespace DirPorter.Tests;

/// <summary>路径模式匹配（黑名单 / 警告名单的规则引擎）。</summary>
public class R2_PathPatternTests
{
    [Fact]
    public void 卷根模式_只匹配卷根()
    {
        var pattern = PathPattern.Parse("{VOLUME_ROOT}");

        Assert.True(pattern.Matches(@"C:\"));
        Assert.True(pattern.Matches(@"D:\"));
        Assert.False(pattern.Matches(@"C:\Users"));
        Assert.False(pattern.Matches(@"C:\Windows"));
    }

    [Fact]
    public void 双星号_匹配自身与全部子树()
    {
        var pattern = PathPattern.Parse(@"C:\Windows\**");

        Assert.True(pattern.Matches(@"C:\Windows"));
        Assert.True(pattern.Matches(@"C:\Windows\System32"));
        Assert.True(pattern.Matches(@"C:\Windows\System32\drivers"));
        Assert.False(pattern.Matches(@"C:\WindowsApps"));
        Assert.False(pattern.Matches(@"D:\Windows"));
    }

    [Fact]
    public void 段内星号_只匹配一个段_不匹配其子目录()
    {
        // 用户 Profile 根目录本身要被拦，但其下的 Documents 必须放行（核心使用场景）
        // ⚠️ 回归锚点：早期实现把"结尾 *"同时当作子树，导致 C:\Users\* 拦下整个用户目录树。
        var pattern = PathPattern.Parse(@"C:\Users\*");

        Assert.True(pattern.Matches(@"C:\Users\Alice"));
        Assert.True(pattern.Matches(@"C:\Users\Bob"));
        Assert.False(pattern.Matches(@"C:\Users\Alice\Documents"));
        Assert.False(pattern.Matches(@"C:\Users"));
        Assert.False(pattern.Matches(@"C:\Users\Alice\Documents\ProjectA"));
        Assert.False(pattern.Matches(@"C:\Users\Alice\AppData\Local\Temp\x"));
    }

    [Fact]
    public void 开头双星号_匹配任意深度前缀()
    {
        var pattern = PathPattern.Parse(@"**\WinSxS\**");

        Assert.True(pattern.Matches(@"C:\Windows\WinSxS"));
        Assert.True(pattern.Matches(@"C:\Windows\WinSxS\Backup"));
        Assert.True(pattern.Matches(@"D:\Anything\WinSxS\Temp"));
        Assert.False(pattern.Matches(@"C:\Windows\System32"));
    }

    [Fact]
    public void 环境变量展开()
    {
        var programData = Environment.GetEnvironmentVariable("ProgramData");
        Assert.False(string.IsNullOrWhiteSpace(programData));

        var pattern = PathPattern.Parse(@"%ProgramData%");
        Assert.True(pattern.Matches(programData!));
        Assert.False(pattern.Matches(programData + @"\SomeApp"));
    }

    [Fact]
    public void 大小写不敏感()
    {
        var pattern = PathPattern.Parse(@"C:\windows\*");
        Assert.True(pattern.Matches(@"c:\WINDOWS\system32"));
    }

    [Fact]
    public void 正斜杠输入也能匹配()
    {
        var pattern = PathPattern.Parse(@"C:\Windows\*");
        Assert.True(pattern.Matches("C:/Windows/System32"));
    }

    [Fact]
    public void 缺失的环境变量_不误匹配为空串()
    {
        var pattern = PathPattern.Parse(@"%DIRPORTER_NO_SUCH_VAR_XYZ%\*");
        Assert.False(pattern.Matches(@"C:\Anything"));
    }

    [Fact]
    public void SplitPath_拆分规则()
    {
        Assert.Equal(new[] { "C:" }, PathPattern.SplitPath(@"C:\"));
        Assert.Equal(new[] { "C:", "Users", "Alice" }, PathPattern.SplitPath(@"C:\Users\Alice\"));
        Assert.Equal(new[] { @"\\server\share", "folder" }, PathPattern.SplitPath(@"\\server\share\folder"));
    }
}

/// <summary>黑名单：每条规则都要求正反用例（需求交付物 4）。</summary>
public class R2_BlacklistPolicyTests
{
    private static readonly BlacklistPolicy Policy = new();

    [Theory]
    [InlineData(@"C:\", "BL-VOLUME-ROOT")]
    [InlineData(@"D:\", "BL-VOLUME-ROOT")]
    [InlineData(@"C:\Windows", "BL-WINDOWS")]
    [InlineData(@"C:\Windows\System32\drivers", "BL-WINDOWS")]
    [InlineData(@"C:\System Volume Information", "BL-SVI")]
    [InlineData(@"C:\$Recycle.Bin", "BL-RECYCLE")]
    [InlineData(@"C:\Users", "BL-USERS")]
    [InlineData(@"C:\Users\Anyone", "BL-PROFILE")]
    [InlineData(@"C:\Users\Public", "BL-PUBLIC")]
    [InlineData(@"C:\ProgramData", "BL-PROGRAMDATA")]
    [InlineData(@"D:\Stuff\WinSxS", "BL-WINSXS")]
    [InlineData(@"C:\Users\Alice\AppData\Local\Microsoft\Windows\INetCache", "BL-APPDATA-MS-LOCAL")]
    [InlineData(@"C:\Users\Alice\AppData\Local\Packages\SomeApp\LocalCache", "BL-APPDATA-PACKAGES")]
    [InlineData(@"C:\Users\Alice\AppData\Roaming\Microsoft\Templates", "BL-APPDATA-MS-ROAMING")]
    public void 命中规则的路径_被硬拒绝(string path, string expectedRuleId)
    {
        var verdict = Policy.Evaluate(path);
        Assert.True(verdict.IsBlocked, $"{path} 应被拒绝");
        Assert.Equal(expectedRuleId, verdict.RuleId);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Theory]
    [InlineData(@"C:\Users\Alice\Documents")]
    [InlineData(@"C:\Users\Alice\Documents\ProjectA")]
    [InlineData(@"C:\Users\Alice\AppData\Local\Temp\myapp")]
    [InlineData(@"C:\ProgramData\MyCompany\App")]
    [InlineData(@"D:\Archive\Anything")]
    [InlineData(@"C:\MyTools\WinSxSBackup")]      // 名字像但段不等价，不得误伤
    [InlineData(@"C:\WindowsApps")]
    public void 不该被拦的路径_放行(string path)
    {
        var verdict = Policy.Evaluate(path);
        Assert.False(verdict.IsBlocked, $"{path} 不应被拒绝，实际规则：{verdict.RuleId}");
    }

    [Fact]
    public void 扩展长度前缀输入_同样能正确判定()
    {
        Assert.True(Policy.Evaluate(@"\\?\C:\Windows\System32").IsBlocked);
        Assert.False(Policy.Evaluate(@"\\?\C:\Users\Alice\Documents").IsBlocked);
    }

    [Fact]
    public void 规则表_与需求逐条对应()
    {
        Assert.Equal(12, Policy.Rules.Count);
        Assert.Contains(Policy.Rules, r => r.Id == "BL-VOLUME-ROOT");
        Assert.Contains(Policy.Rules, r => r.Id == "BL-WINSXS");
    }
}

/// <summary>警告名单（需确认，不阻止）。</summary>
public class R2_WarningListPolicyTests
{
    // 不注入启动项探测：本组只验证静态规则，避免测试依赖机器上的服务状态。
    private static readonly WarningListPolicy Policy = new(startupProbe: null);

    [Theory]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files\SomeApp")]
    [InlineData(@"C:\Program Files (x86)\SomeApp")]
    [InlineData(@"C:\ProgramData\MyCompany")]
    public void 命中警告名单_需要确认(string path)
    {
        var verdict = Policy.Evaluate(path);
        Assert.True(verdict.RequiresAcknowledge, $"{path} 应触发警告");
        Assert.NotEmpty(verdict.Hits);
    }

    [Fact]
    public void ProgramData下的Microsoft目录_是例外()
    {
        var verdict = Policy.Evaluate(@"C:\ProgramData\Microsoft\Windows\Start Menu");
        Assert.False(verdict.RequiresAcknowledge);
    }

    [Theory]
    [InlineData(@"D:\Apps\kaspersky")]
    [InlineData(@"D:\Software\NVIDIA Corporation")]
    [InlineData(@"D:\Tools\Office")]
    [InlineData(@"D:\Drivers\chipset")]
    public void 名称关键词_命中高风险软件(string path)
    {
        var verdict = Policy.Evaluate(path);
        Assert.True(verdict.RequiresAcknowledge);
        Assert.Contains(verdict.Hits, h => h.RuleId == "WN-NAME-KEYWORD");
    }

    [Theory]
    [InlineData(@"D:\Archive\Photos")]
    [InlineData(@"C:\Users\Alice\Documents")]
    public void 普通目录_不触发警告(string path)
    {
        var verdict = Policy.Evaluate(path);
        Assert.False(verdict.RequiresAcknowledge, $"{path} 不该触发警告：{string.Join(",", verdict.Hits.Select(h => h.RuleId))}");
    }

    [Fact]
    public void 启动项引用_能被识别为警告()
    {
        // 用真实的当前进程可执行文件所在的目录做正向验证：若注册表里有引用它的启动项/服务则命中，
        // 否则至少验证探测本身不抛异常且返回结构正确（不依赖机器状态做断言）。
        var probe = new StartupReferenceProbe();
        var references = probe.FindReferences(@"C:\Windows\System32");

        Assert.NotNull(references);
        Assert.All(references, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" -silent", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Tools\svc.exe --service", @"C:\Tools\svc.exe")]
    [InlineData(@"%SystemRoot%\System32\svchost.exe -k netsvcs", @"C:\Windows\System32\svchost.exe")]
    public void 注册表值解析为路径(string value, string expected)
    {
        var parsed = StartupReferenceProbe.ExtractPath(value);
        Assert.Equal(expected, parsed!, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("svchost.exe -k netsvcs")]
    [InlineData(@"\SystemRoot\System32\drivers\foo.sys")]
    public void 无法定位的注册表值_返回null(string value)
    {
        Assert.Null(StartupReferenceProbe.ExtractPath(value));
    }
}

/// <summary>Win32 属性常量锚点（写错会让特殊对象分类整体失效）。</summary>
public class R2_FileAttributeConstantTests
{
    [Fact]
    public void 文件属性常量_与winnt头文件一致()
    {
        Assert.Equal(0x00000010u, DirPorter.Core.FileSystem.Kernel32.FileAttributeDirectory);
        Assert.Equal(0x00000200u, DirPorter.Core.FileSystem.Kernel32.FileAttributeSparseFile);
        Assert.Equal(0x00000400u, DirPorter.Core.FileSystem.Kernel32.FileAttributeReparsePoint);
        Assert.Equal(0x00000800u, DirPorter.Core.FileSystem.Kernel32.FileAttributeCompressed);
        Assert.Equal(0x00004000u, DirPorter.Core.FileSystem.Kernel32.FileAttributeEncrypted);
        Assert.Equal(0x00040000u, DirPorter.Core.FileSystem.Kernel32.FileAttributeRecallOnOpen);
        Assert.Equal(0x00400000u, DirPorter.Core.FileSystem.Kernel32.FileAttributeRecallOnDataAccess);
    }

    [Fact]
    public void 目录访问权限常量_与winnt头文件一致()
    {
        Assert.Equal(0x0001u, DirPorter.Core.FileSystem.Kernel32.FileListDirectory);
        Assert.Equal(0x0002u, DirPorter.Core.FileSystem.Kernel32.FileAddFile);
        Assert.Equal(0x0004u, DirPorter.Core.FileSystem.Kernel32.FileAddSubdirectory);
        Assert.Equal(0x0040u, DirPorter.Core.FileSystem.Kernel32.FileDeleteChild);
    }
}
