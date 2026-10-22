using DirPorter.App.Services;
using DirPorter.Core.Links;
using Xunit;

namespace DirPorter.Tests;

/// <summary>R12.6：迁移=剪切（默认不留备份）+ 检查更新的版本比较。</summary>
[Collection(DataRootSequentialCollection.Name)]
public class R15_CutSemanticsAndUpdateTests
{
    private sealed class SilentInteraction : IUserInteraction
    {
        public void Info(string message, string? title = null) { }
        public void Error(string message, string? title = null) { }
        public bool Confirm(string message, string? title = null) => true;
        public string? PickFolder(string? initialDirectory, string title) => null;
        public void OpenInExplorer(string path) { }
        public void CopyToClipboard(string text) { }
    }

    [Fact]
    public void 默认不保留备份_即剪切语义()
    {
        var host = new AppHost(new SilentInteraction(), new UiSettings());
        Assert.False(host.Main.Migrate.KeepBackup, "默认应为剪切：迁移成功后不保留源目录副本。");
    }

    [Fact]
    public async Task 纯迁移后_源位置与停车区都不留第二份数据()
    {
        var root = Path.Combine(Path.GetTempPath(), "dp-r15-cut-" + Guid.NewGuid().ToString("N")[..6]);
        var source = Path.Combine(root, "Src");
        var targetRoot = Path.Combine(root, "Target");

        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "alpha");
        Directory.CreateDirectory(targetRoot);

        using var guard = new DataRootGuard();

        try
        {
            var host = new AppHost(new SilentInteraction(), new UiSettings());
            var migrate = host.Main.Migrate;
            Assert.False(migrate.KeepBackup);

            migrate.TargetRoot = targetRoot;
            migrate.AcknowledgeWarnings = true;
            migrate.ReplaceQueue(new[] { source }, LinkKind.Junction, createLink: false);

            await migrate.StartCommand.ExecuteAsync(null);

            Assert.False(Directory.Exists(source), "剪切后源目录不应存在。");
            Assert.True(File.Exists(Path.Combine(targetRoot, "Src", "a.txt")), "目标应持有数据。");

            var parking = Path.Combine(root, ".migbak");
            if (Directory.Exists(parking))
            {
                var leftovers = Directory.EnumerateDirectories(parking)
                    .Where(d => !string.Equals(Path.GetFileName(d), ".ledger", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                Assert.Empty(leftovers);
            }

            var parkingCopy = Path.Combine(parking, "Src");
            Assert.False(Directory.Exists(parkingCopy), "停车区不应残留源目录副本。");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [InlineData("0.1.8", "0.1.7", true)]
    [InlineData("v0.2.0", "0.1.7", true)]
    [InlineData("0.1.7", "0.1.7", false)]
    [InlineData("0.1.6", "0.1.7", false)]
    [InlineData("0.2.0-preview", "0.1.7", true)]
    public void 版本比较_按语义版本判断是否更新(string remote, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewer(remote, Version.Parse(current)));
    }
}
