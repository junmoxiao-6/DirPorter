using DirPorter.App.Services;
using DirPorter.App.ViewModels;
using DirPorter.Core.Config;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;
using Xunit;

namespace DirPorter.Tests;

/// <summary>R12.5：队列移除 / 停车区清点删除 / 链接总览扫描与还原。</summary>
[Collection(DataRootSequentialCollection.Name)]
public class R14_QueueParkingLinkOverviewTests
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
    public void 队列_可以移除单条且保留其余()
    {
        var host = new AppHost(new SilentInteraction(), new UiSettings());
        var migrate = host.Main.Migrate;

        migrate.ReplaceQueue(new[] { @"C:\tmp\a", @"C:\tmp\b" }, LinkKind.Junction, createLink: false);
        Assert.Equal(2, migrate.Queue.Count);

        var first = migrate.Queue[0];
        migrate.RemoveFromQueueCommand.Execute(first);

        Assert.Single(migrate.Queue);
        Assert.DoesNotContain(first, migrate.Queue);
    }

    [Fact]
    public void 停车区_清点与删除_必须保留台账归档()
    {
        var root = Path.Combine(Path.GetTempPath(), ".migbak-r14-" + Guid.NewGuid().ToString("N")[..6]);
        var bak = Path.Combine(root, ".migbak");
        Directory.CreateDirectory(Path.Combine(bak, "Coze_20261017"));
        Directory.CreateDirectory(Path.Combine(bak, ".ledger"));
        File.WriteAllText(Path.Combine(bak, "Coze_20261017", "a.txt"), "x");

        try
        {
            var found = ParkingInventory.Scan(new[] { bak });
            Assert.Single(found);
            Assert.Equal(1, found[0].EntryCount);          // .ledger 不计入条目数

            var (deleted, failed) = ParkingInventory.Delete(bak);
            Assert.Equal(1, deleted);
            Assert.Equal(0, failed);
            Assert.False(Directory.Exists(Path.Combine(bak, "Coze_20261017")));
            Assert.True(Directory.Exists(Path.Combine(bak, ".ledger")), "台账归档必须保留。");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void 链接总览_能扫到目录联接_并能还原()
    {
        var root = Path.Combine(Path.GetTempPath(), "dp-r14-link-" + Guid.NewGuid().ToString("N")[..6]);
        var target = Path.Combine(root, "Target");
        var link = Path.Combine(root, "Link");

        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "data.txt"), "hello");
        Directory.CreateDirectory(root);

        try
        {
            var links = LinkService.CreateDefault();
            var created = links.Create(new LinkRequest(link, target, LinkKind.Junction), LinkCreateOptions.Default);
            Assert.True(created.Capability.CanHandle, "创建目录联接失败：" + created.Capability.Reason);

            var scanned = LinkScanner.Scan(new[] { root }, links);
            Assert.Contains(scanned, l => string.Equals(l.LinkPath, link, StringComparison.OrdinalIgnoreCase)
                                          && l.Kind == LinkKind.Junction);

            var result = LinkRestorer.Restore(link, links);
            Assert.True(result.Success, result.Message);

            Assert.False(links.Inspect(link).IsKnownLink, "还原后链接本体应已移除。");
            Assert.True(File.Exists(Path.Combine(link, "data.txt")), "数据应已回到链接位置。");
            Assert.True(Directory.Exists(target), "目标目录本身不应被删除。");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void 表格_禁止手动拖动列宽_避免联动()
    {
        var shared = File.ReadAllText(Path.Combine(LocateAppDirectory(), "Themes", "Shared.xaml"));
        Assert.Contains("CanUserResizeColumns", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void 导航_包含链接总览且位于记录与设置之间()
    {
        var host = new AppHost(new SilentInteraction(), new UiSettings());
        var keys = host.Main.NavItems.Select(static n => n.Key).ToList();

        var history = keys.IndexOf("history");
        var links = keys.IndexOf("links");
        var settings = keys.IndexOf("settings");

        Assert.True(links > history && links < settings, "链接总览应位于记录与设置之间。");
    }

    private static string LocateAppDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DirPorter.sln")))
            {
                var app = Path.Combine(directory.FullName, "src", "DirPorter.App");
                if (Directory.Exists(app)) return app;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到 DirPorter.App 源码目录。");
    }
}
