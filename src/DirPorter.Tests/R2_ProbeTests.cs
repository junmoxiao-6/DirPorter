using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using Xunit;

namespace DirPorter.Tests;

/// <summary>目录树探测：聚合正确性 + 特殊对象识别 + 嵌套链接不递归。</summary>
public class R2_TreeProbeTests
{
    private static TreeProbeResult Probe(string path, TreeProbeOptions? options = null)
        => TreeProbe.Probe(path, options ?? TreeProbeOptions.Default, null, CancellationToken.None);

    [Fact]
    public void 统计文件数与字节数()
    {
        using var ws = new TempWorkspace("probe-count");
        ws.CreateFile(@"A\a1.txt", new string('x', 100));
        ws.CreateFile(@"A\a2.txt", new string('x', 200));
        ws.CreateFile(@"A\B\b1.txt", new string('x', 300));
        ws.CreateDirectory(@"A\Empty");

        var result = Probe(ws.Root);

        Assert.Equal(3, result.FileCount);
        Assert.Equal(3, result.DirectoryCount); // A、A\B、A\Empty
        Assert.Equal(600, result.LogicalBytes);
        Assert.Equal(300, result.MaxFileBytes);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void 嵌套Junction_被标注且不递归进入()
    {
        using var ws = new TempWorkspace("probe-junction");
        var realData = ws.CreateDirectory(@"Inner\RealData");
        ws.CreateFile(@"Inner\RealData\payload.bin", new string('y', 5000));
        var link = ws.PathOf(@"Inner\LinkToData");

        new JunctionLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(link, realData), LinkCreateOptions.Default);

        var result = Probe(ws.Root);

        // 必须只统计到一份数据（若不跳过重解析点，会顺着链接把 RealData 再算一遍）
        Assert.Equal(1, result.CountOf(SpecialObjectKind.NestedDirectoryReparsePoint));
        Assert.Contains(result.SpecialObjects, s => s.Kind == SpecialObjectKind.NestedDirectoryReparsePoint);
        Assert.Equal(5000, result.LogicalBytes);
    }

    [Fact]
    public void 硬链接文件_被标注()
    {
        using var ws = new TempWorkspace("probe-hardlink");
        var target = ws.CreateFile("real.txt", "shared");
        new HardLinkProvider(new Win32VolumeResolver())
            .Create(new LinkRequest(ws.PathOf("link.txt"), target), LinkCreateOptions.Default);

        var result = Probe(ws.Root);

        Assert.True(result.CountOf(SpecialObjectKind.HardLinkFile) >= 1);
        Assert.True(result.DeepProbeApplied);
        Assert.False(result.DeepProbeSkippedForScale);
    }

    [Fact]
    public void 备用数据流_被标注()
    {
        using var ws = new TempWorkspace("probe-ads");
        var target = ws.CreateFile("withads.txt", "base");

        // 在当前卷（NTFS）上创建一个备用数据流
        using (var stream = new FileStream(target + ":secret", FileMode.Create, FileAccess.Write))
        {
            stream.Write(new byte[] { 1, 2, 3, 4 });
        }

        var result = Probe(ws.Root);

        Assert.Equal(1, result.CountOf(SpecialObjectKind.AlternateDataStream));
    }

    [Fact]
    public void 关闭ADS探测_则不再标注()
    {
        using var ws = new TempWorkspace("probe-ads-off");
        var target = ws.CreateFile("withads.txt", "base");
        using (var stream = new FileStream(target + ":secret", FileMode.Create, FileAccess.Write))
        {
            stream.Write(new byte[] { 1, 2, 3, 4 });
        }

        var result = Probe(ws.Root, new TreeProbeOptions { DetectAlternateDataStreams = false });

        Assert.Equal(0, result.CountOf(SpecialObjectKind.AlternateDataStream));
    }

    [Fact]
    public void 超长路径_被标注()
    {
        using var ws = new TempWorkspace("probe-longpath");
        var longDir = PathNormalizer.NormalizeAbsolute(ws.PathOf(new string('a', 120)));
        var deeper = PathNormalizer.Combine(longDir, new string('b', 120));

        Assert.True(deeper.Length > 260, "构造的长路径应超过 260 字符");
        Directory.CreateDirectory(deeper);
        File.WriteAllText(PathNormalizer.Combine(deeper, "f.txt"), "x");

        var result = Probe(ws.Root);

        Assert.True(result.CountOf(SpecialObjectKind.LongPath) >= 1);
    }

    [Fact]
    public void 文件数超过上限_昂贵探测如实降级()
    {
        using var ws = new TempWorkspace("probe-limit");
        for (var i = 0; i < 12; i++) ws.CreateFile($@"f{i}.txt", "x");

        var result = Probe(ws.Root, new TreeProbeOptions { DeepProbeFileLimit = 5 });

        Assert.True(result.DeepProbeSkippedForScale);
        Assert.False(result.DeepProbeApplied);
    }

    [Fact]
    public void 样本数量受上限约束_不把全部条目常驻内存()
    {
        using var ws = new TempWorkspace("probe-samples");
        for (var i = 0; i < 30; i++)
        {
            using var stream = new FileStream(ws.CreateFile($@"f{i}.txt", "x") + ":ads", FileMode.Create, FileAccess.Write);
            stream.WriteByte(7);
        }

        var result = Probe(ws.Root, new TreeProbeOptions { MaxSamplesPerKind = 3 });

        var ads = result.SpecialObjects.Single(s => s.Kind == SpecialObjectKind.AlternateDataStream);
        Assert.Equal(30, ads.Count);
        Assert.Equal(3, ads.Samples.Count);
    }

    [Fact]
    public void 不存在的路径_返回空结果并记录错误()
    {
        using var ws = new TempWorkspace("probe-missing");
        var result = Probe(ws.PathOf("nope"));

        Assert.Equal(0, result.FileCount);
        Assert.NotEmpty(result.Errors);
    }
}

/// <summary>空间计算。</summary>
public class R2_SpaceCalculatorTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void 空间充足_系数1_05()
    {
        var volumes = new FakeVolumeResolver { FreeBytes = 10 * Gb };
        var requirement = SpaceCalculator.Evaluate(@"C:\target", 4 * Gb, volumes);

        Assert.True(requirement.IsSatisfied);
        Assert.Equal((long)Math.Ceiling(4 * Gb * 1.05), requirement.RequiredBytes);
        Assert.Equal(0, requirement.ShortfallBytes);
        Assert.Contains("空间充足", requirement.Describe());
    }

    [Fact]
    public void 空间不足_给出缺口字节数()
    {
        var volumes = new FakeVolumeResolver { FreeBytes = 2 * Gb };
        var requirement = SpaceCalculator.Evaluate(@"C:\target", 4 * Gb, volumes);

        Assert.False(requirement.IsSatisfied);
        Assert.Equal((long)Math.Ceiling(4 * Gb * 1.05) - 2 * Gb, requirement.ShortfallBytes);
        Assert.Contains("空间不足", requirement.Describe());
        Assert.Contains("缺少", requirement.Describe());
    }

    [Fact]
    public void 系数小于1_抛参数异常()
    {
        var volumes = new FakeVolumeResolver();
        Assert.Throws<ArgumentOutOfRangeException>(() => SpaceCalculator.Evaluate(@"C:\t", 1, volumes, 0.9));
    }

    [Fact]
    public void 批次级_按卷累计后判定()
    {
        var volumes = new FakeVolumeResolver { FreeBytes = 3 * Gb };

        // 单条都放得下（2GB × 1.05 = 2.1GB < 3GB），累计放不下（4.2GB > 3GB）
        var requirements = SpaceCalculator.EvaluateBatch(
            new[] { (@"C:\a", 2 * Gb), (@"C:\b", 2 * Gb) },
            volumes);

        var requirement = Assert.Single(requirements);
        Assert.False(requirement.IsSatisfied);
        Assert.Equal(@"C:\", requirement.VolumeRoot);
    }

    [Fact]
    public void 格式化为人类可读()
    {
        Assert.Equal("0 B", SpaceRequirement.Format(0));
        Assert.Equal("1 KB", SpaceRequirement.Format(1024));
        Assert.Equal("2 GB", SpaceRequirement.Format(2 * Gb));
    }
}

/// <summary>权限探测。</summary>
public class R2_PermissionProbeTests
{
    [Fact]
    public void 普通临时目录_三项权限齐全()
    {
        using var ws = new TempWorkspace("perm-ok");
        var dir = ws.CreateDirectory("Source");

        var result = PermissionProbe.ProbeSource(PathNormalizer.NormalizeAbsolute(dir));

        Assert.True(result.CanList, result.Detail);
        Assert.True(result.CanDelete, result.Detail);
        Assert.True(result.CanAddChild, result.Detail);
        Assert.True(result.AllGranted);
        Assert.Contains("具备", result.Detail);
    }

    [Fact]
    public void 不存在的路径_不可读不可删()
    {
        using var ws = new TempWorkspace("perm-missing");
        var result = PermissionProbe.ProbeSource(PathNormalizer.NormalizeAbsolute(ws.PathOf("nope")));

        Assert.False(result.CanList);
        Assert.False(result.CanDelete);
        Assert.False(result.AllGranted);
        Assert.NotNull(result.Win32Code);
    }

    [Fact]
    public void 目标父目录不存在_向上找最近可写祖先()
    {
        using var ws = new TempWorkspace("perm-parent");
        var deepTarget = ws.PathOf(@"A\B\C\D");

        var result = PermissionProbe.ProbeTargetParent(PathNormalizer.NormalizeAbsolute(deepTarget));

        Assert.True(result.CanAddChild, result.Detail);
        Assert.Equal(ws.Root, result.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("自动创建缺失层级", result.Detail);
    }

    [Fact]
    public void 完全不可达的路径_判定为不可写()
    {
        var result = PermissionProbe.ProbeTargetParent(PathNormalizer.NormalizeAbsolute(@"Z:\NoSuchVolume\Deep\Target"));
        Assert.False(result.CanAddChild);
        Assert.NotEqual(0, result.Win32Code);
    }
}
