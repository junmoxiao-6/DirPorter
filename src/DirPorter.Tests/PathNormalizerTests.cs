using DirPorter.Core.FileSystem;
using Xunit;

namespace DirPorter.Tests;

public class PathNormalizerTests
{
    [Fact]
    public void NormalizeAbsolute_盘符路径_加扩展前缀()
    {
        var result = PathNormalizer.NormalizeAbsolute(@"C:\Temp\DirPorter");
        Assert.Equal(@"\\?\C:\Temp\DirPorter", result);
    }

    [Fact]
    public void NormalizeAbsolute_幂等_重复调用结果不变()
    {
        var once = PathNormalizer.NormalizeAbsolute(@"C:\Temp\DirPorter");
        var twice = PathNormalizer.NormalizeAbsolute(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void NormalizeAbsolute_正斜杠统一为反斜杠()
    {
        Assert.Equal(@"\\?\C:\Temp\A\B", PathNormalizer.NormalizeAbsolute("C:/Temp/A/B"));
    }

    [Fact]
    public void NormalizeAbsolute_UNC路径_转为UNC扩展前缀()
    {
        Assert.Equal(@"\\?\UNC\server\share\folder", PathNormalizer.NormalizeAbsolute(@"\\server\share\folder"));
    }

    [Fact]
    public void NormalizeAbsolute_相对路径_解析为完全限定()
    {
        var result = PathNormalizer.NormalizeAbsolute("sub\\file");
        Assert.StartsWith(@"\\?\", result);
        Assert.EndsWith(@"sub\file", result);
    }

    [Fact]
    public void NormalizeAbsolute_超长路径_不做GetFullPath_不抛异常()
    {
        var longPath = @"C:\" + new string('a', 300) + @"\" + new string('b', 300);
        var result = PathNormalizer.NormalizeAbsolute(longPath);
        Assert.Equal(@"\\?\" + longPath, result);
        Assert.True(result.Length > 260);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeAbsolute_空路径_抛参数异常(string input)
    {
        Assert.Throws<ArgumentException>(() => PathNormalizer.NormalizeAbsolute(input));
    }

    [Fact]
    public void NormalizeAbsolute_含非法字符_抛参数异常()
    {
        Assert.Throws<ArgumentException>(() => PathNormalizer.NormalizeAbsolute(@"C:\a|b"));
    }

    [Fact]
    public void StripExtendedPrefix_与Normalize往返一致()
    {
        const string original = @"C:\Temp\DirPorter";
        var normalized = PathNormalizer.NormalizeAbsolute(original);
        Assert.Equal(original, PathNormalizer.StripExtendedPrefix(normalized));
    }

    [Fact]
    public void StripExtendedPrefix_UNC往返一致()
    {
        const string original = @"\\server\share\folder";
        var normalized = PathNormalizer.NormalizeAbsolute(original);
        Assert.Equal(original, PathNormalizer.StripExtendedPrefix(normalized));
    }

    [Theory]
    [InlineData(@"C:\", @"\\?\C:\")]
    [InlineData(@"C:\Temp\", @"\\?\C:\Temp")]
    [InlineData(@"C:\Temp", @"\\?\C:\Temp")]
    public void TrimTrailingSeparator_卷根保留分隔符_普通目录去掉(string input, string expected)
    {
        Assert.Equal(expected, PathNormalizer.NormalizeAbsolute(input));
    }

    [Fact]
    public void EnsureTrailingSeparator_只加一个()
    {
        Assert.Equal(@"C:\a\", PathNormalizer.EnsureTrailingSeparator(@"C:\a"));
        Assert.Equal(@"C:\a\", PathNormalizer.EnsureTrailingSeparator(@"C:\a\"));
    }

    [Fact]
    public void Combine_拼接子项()
    {
        Assert.Equal(@"\\?\C:\a\b\c", PathNormalizer.Combine(PathNormalizer.NormalizeAbsolute(@"C:\a\b"), "c"));
    }

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a\b", true)]      // 相同
    [InlineData(@"C:\a\b\c", @"C:\a\b", true)]    // 子路径
    [InlineData(@"C:\a\b", @"C:\a\b\c", false)]   // 父路径不算在其下
    [InlineData(@"C:\ab", @"C:\a", false)]        // 前缀相同但不是路径边界
    [InlineData(@"C:\a\b2", @"C:\a\b", false)]    // 兄弟目录
    [InlineData(@"C:\A\B", @"C:\a\b", true)]      // 大小写不敏感
    public void IsSameOrUnder_各种边界(string candidate, string ancestor, bool expected)
    {
        Assert.Equal(expected, PathNormalizer.IsSameOrUnder(candidate, ancestor));
    }

    [Fact]
    public void IsSameOrUnder_扩展前缀与普通路径混用_仍正确()
    {
        Assert.True(PathNormalizer.IsSameOrUnder(@"C:\a\b\c", @"\\?\C:\a\b"));
        Assert.True(PathNormalizer.IsSameOrUnder(@"\\?\C:\a\b\c", @"C:\a\b"));
    }

    [Fact]
    public void IsSameOrUnder_超长路径_不误判()
    {
        var deep = @"C:\" + new string('x', 200);
        var deeper = deep + @"\y";
        Assert.True(PathNormalizer.IsSameOrUnder(deeper, deep));
        Assert.False(PathNormalizer.IsSameOrUnder(deep, deeper));
    }

    [Theory]
    [InlineData(@"C:\Temp\a", @"C:\")]
    [InlineData(@"D:\", @"D:\")]
    public void TryGetVolumeRoot_盘符(string path, string expected)
    {
        Assert.Equal(expected, PathNormalizer.TryGetVolumeRoot(path));
    }

    [Fact]
    public void TryGetVolumeRoot_UNC_取共享根()
    {
        Assert.Equal(@"\\server\share", PathNormalizer.TryGetVolumeRoot(@"\\server\share\a\b"));
    }

    [Theory]
    [InlineData(@"C:\a\b", true)]
    [InlineData(@"C:\", true)]
    [InlineData(@"D:\", true)]
    [InlineData(@"a\b", false)]
    [InlineData(@".\rel", false)]
    public void IsDriveQualified_判定(string path, bool expected)
    {
        Assert.Equal(expected, PathNormalizer.IsDriveQualified(path));
    }

    [Theory]
    [InlineData(@"\\server\share\a", true)]
    [InlineData(@"\\?\UNC\server\share", true)]
    [InlineData(@"C:\a", false)]
    public void IsUnc_判定(string path, bool expected)
    {
        Assert.Equal(expected, PathNormalizer.IsUnc(path));
    }
}
