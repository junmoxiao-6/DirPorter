using System.Text;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using Xunit;

namespace DirPorter.Tests;

public class ReparseDataTests
{
    [Fact]
    public void FSCTL常量_与winioctl头文件一致()
    {
        // 由 CTL_CODE 公式推导，避免手抄错误：
        // CTL_CODE(FILE_DEVICE_FILE_SYSTEM=0x0009, 41/42/43, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
        Assert.Equal(0x000900A4u, Kernel32.FsctlSetReparsePoint);
        Assert.Equal(0x000900A8u, Kernel32.FsctlGetReparsePoint);
        Assert.Equal(0x000900ACu, Kernel32.FsctlDeleteReparsePoint);
    }

    [Fact]
    public void 重解析标签常量_与winnt头文件一致()
    {
        Assert.Equal(0xA0000003u, ReparseData.TagMountPoint);
        Assert.Equal(0xA000000Cu, ReparseData.TagSymlink);
    }

    [Fact]
    public void BuildMountPoint_头部字段与偏移正确()
    {
        var buffer = ReparseData.BuildMountPoint(@"\??\C:\Target", @"C:\Target");

        Assert.Equal(0xA0000003u, BitConverter.ToUInt32(buffer.AsSpan(0, 4)));          // ReparseTag
        Assert.Equal(0, BitConverter.ToUInt16(buffer.AsSpan(6, 2)));                    // Reserved
        Assert.Equal(16, ReparseData.MountPointPathBufferOffset);

        var reparseDataLength = BitConverter.ToUInt16(buffer.AsSpan(4, 2));
        Assert.Equal(8 + ("\\??\\C:\\Target".Length + 1 + "C:\\Target".Length + 1) * 2, reparseDataLength);
        Assert.Equal(8 + reparseDataLength, buffer.Length);

        Assert.Equal(0, BitConverter.ToUInt16(buffer.AsSpan(8, 2)));                     // SubstituteNameOffset
        Assert.Equal("\\??\\C:\\Target".Length * 2, BitConverter.ToUInt16(buffer.AsSpan(10, 2)));
        Assert.Equal(("\\??\\C:\\Target".Length + 1) * 2, BitConverter.ToUInt16(buffer.AsSpan(12, 2))); // PrintNameOffset
        Assert.Equal("C:\\Target".Length * 2, BitConverter.ToUInt16(buffer.AsSpan(14, 2)));
    }

    [Fact]
    public void BuildMountPoint_与TryParse往返一致()
    {
        var buffer = ReparseData.BuildMountPoint(@"\??\C:\目标 dir", @"C:\目标 dir");
        Assert.True(ReparseData.TryParse(buffer, out var parsed));
        Assert.Equal(ReparseData.TagMountPoint, parsed.Tag);
        Assert.Equal(@"\??\C:\目标 dir", parsed.SubstituteName);
        Assert.Equal(@"C:\目标 dir", parsed.PrintName);
        Assert.False(parsed.IsRelative);
    }

    [Fact]
    public void TryParse_未知标签_返回false()
    {
        var buffer = new byte[24];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), 0x80000017u); // 其它重解析标签
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 2), (ushort)16);
        Assert.False(ReparseData.TryParse(buffer, out _));
    }

    [Fact]
    public void TryParse_缓冲区被截断_返回false()
    {
        var buffer = ReparseData.BuildMountPoint(@"\??\C:\T", @"C:\T");
        Assert.False(ReparseData.TryParse(buffer.AsSpan(0, 10), out _));
        Assert.False(ReparseData.TryParse(Array.Empty<byte>(), out _));
    }

    [Fact]
    public void TryParse_符号链接缓冲区_解析目标与相对标志()
    {
        // 手工构造 SYMLINK 重解析缓冲区：头部 8 + 固定字段 12 + PathBuffer
        const string substitute = @"..\..\Target dir";
        const string print = @"..\..\Target dir";
        var subBytes = Encoding.Unicode.GetBytes(substitute);
        var printBytes = Encoding.Unicode.GetBytes(print);
        var pathBufferLength = subBytes.Length + 2 + printBytes.Length + 2;
        var buffer = new byte[ReparseData.SymlinkPathBufferOffset + pathBufferLength];

        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), ReparseData.TagSymlink);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 2), (ushort)(12 + pathBufferLength));
        BitConverter.TryWriteBytes(buffer.AsSpan(6, 2), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 2), (ushort)0);                      // SubstituteNameOffset
        BitConverter.TryWriteBytes(buffer.AsSpan(10, 2), (ushort)subBytes.Length);        // SubstituteNameLength
        BitConverter.TryWriteBytes(buffer.AsSpan(12, 2), (ushort)(subBytes.Length + 2));  // PrintNameOffset
        BitConverter.TryWriteBytes(buffer.AsSpan(14, 2), (ushort)printBytes.Length);      // PrintNameLength
        BitConverter.TryWriteBytes(buffer.AsSpan(16, 4), 1u);                            // SYMLINK_FLAG_RELATIVE

        Buffer.BlockCopy(subBytes, 0, buffer, ReparseData.SymlinkPathBufferOffset, subBytes.Length);
        Buffer.BlockCopy(printBytes, 0, buffer, ReparseData.SymlinkPathBufferOffset + subBytes.Length + 2, printBytes.Length);

        Assert.True(ReparseData.TryParse(buffer, out var parsed));
        Assert.Equal(ReparseData.TagSymlink, parsed.Tag);
        Assert.True(parsed.IsRelative);
        Assert.Equal(substitute, parsed.SubstituteName);
    }

    [Theory]
    [InlineData(@"C:\Target", @"\??\C:\Target", false)]
    [InlineData(@"D:\a\b c", @"\??\D:\a\b c", false)]
    [InlineData(@"\\server\share\dir", @"\??\UNC\server\share\dir", true)]
    public void ToSubstituteName_构造正确(string plain, string expected, bool isUnc)
    {
        Assert.Equal(expected, ReparseData.ToSubstituteName(plain, isUnc));
    }

    [Theory]
    [InlineData(@"\??\C:\Target", @"C:\Target")]
    [InlineData(@"\??\UNC\server\share\dir", @"\\server\share\dir")]
    [InlineData(@"C:\Already\Plain", @"C:\Already\Plain")]
    public void FromSubstituteName_还原正确(string substitute, string expected)
    {
        Assert.Equal(expected, ReparseData.FromSubstituteName(substitute));
    }

    [Fact]
    public void 替换名往返一致_含中文与空格()
    {
        const string plain = @"C:\中文 目录\子层";
        var substitute = ReparseData.ToSubstituteName(plain);
        Assert.Equal(plain, ReparseData.FromSubstituteName(substitute));
    }
}
