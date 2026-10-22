using System.Text;
using DirPorter.Core.Scan;
using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// R5：扫描估算缓存。
///
/// 本组的**核心不是"缓存能用"，而是把它的失效边界写成可执行断言**：
/// 目录级缓存只对"条目集合变化"有效，对"文件内容/大小变化"无效 ——
/// 因此它只能用于预览估算，绝不允许接入迁移决策。
/// </summary>
public class R5_ScanCacheTests
{
    private static string BuildTree(TempWorkspace ws)
    {
        var root = ws.CreateDirectory("Tree");
        ws.CreateFile(@"Tree\a.txt", new string('a', 100));
        ws.CreateFile(@"Tree\sub\b.txt", new string('b', 200));
        return root;
    }

    private static ScanEstimateCache OpenCache(TempWorkspace ws, bool enabled = true)
        => ScanEstimateCache.Open(ws.PathOf("cache.json"), enabled);

    [Fact]
    public void 首次全部未命中_第二次全部命中且结果一致()
    {
        using var ws = new TempWorkspace("r5-cache-hit");
        var root = BuildTree(ws);
        var cache = OpenCache(ws);

        var first = cache.EstimateTree(root);
        Assert.True(first.CacheMisses > 0);
        Assert.Equal(0, first.CacheHits);
        Assert.True(first.IsEstimate);

        var second = cache.EstimateTree(root);
        Assert.True(second.CacheHits > 0);
        Assert.Equal(first.FileCount, second.FileCount);
        Assert.Equal(first.DirectoryCount, second.DirectoryCount);
        Assert.Equal(first.LogicalBytes, second.LogicalBytes);
    }

    [Fact]
    public void 估算结果与实时扫描的计数一致()
    {
        using var ws = new TempWorkspace("r5-cache-parity");
        var root = BuildTree(ws);

        var cache = OpenCache(ws);
        var estimate = cache.EstimateTree(root);
        var actual = DirectoryScanner.Scan(root);

        Assert.Equal(actual.FileCount, estimate.FileCount);
        Assert.Equal(actual.DirectoryCount, estimate.DirectoryCount);
        Assert.Equal(actual.LogicalBytes, estimate.LogicalBytes);
    }

    [Fact]
    public void 目录内新增文件_缓存失效并给出新值()
    {
        using var ws = new TempWorkspace("r5-cache-add");
        var root = BuildTree(ws);
        var cache = OpenCache(ws);

        cache.EstimateTree(root);

        ws.CreateFile(@"Tree\new.txt", new string('n', 50));

        var after = cache.EstimateTree(root);
        Assert.Equal(3, after.FileCount);
        Assert.Equal(350, after.LogicalBytes);
        Assert.True(after.CacheMisses > 0, "新增条目更新了目录的修改时间，缓存必须失效");
    }

    [Fact]
    public void 目录内删除文件_缓存失效()
    {
        using var ws = new TempWorkspace("r5-cache-delete");
        var root = BuildTree(ws);
        var cache = OpenCache(ws);

        cache.EstimateTree(root);
        File.Delete(ws.PathOf(@"Tree\a.txt"));

        var after = cache.EstimateTree(root);
        Assert.Equal(1, after.FileCount);
        Assert.Equal(200, after.LogicalBytes);
    }

    [Fact]
    public void 目录改名_旧目录缓存判定为已消失()
    {
        using var ws = new TempWorkspace("r5-cache-gone");
        var root = BuildTree(ws);
        var cache = OpenCache(ws);

        cache.EstimateTree(root);
        Directory.Move(ws.PathOf(@"Tree\sub"), ws.PathOf(@"Tree\sub2"));

        // 父目录 mtime 变化 → 重新读取；旧子目录路径已不存在
        var after = cache.EstimateTree(root);
        Assert.Equal(2, after.FileCount);
    }

    /// <summary>
    /// ⚠️ **已知局限的可执行版本**：
    /// 改写文件内容 / 大小**不会**更新父目录的修改时间，因此目录级缓存仍然命中，报出的字节数是过期值。
    /// 这正是"估算缓存绝不能用于 PRECHECK / 校验 / 空间复检"的原因。
    /// 如果哪天 Windows 改了行为（此用例失败），说明局部可以放宽——但宽松化必须由这条断言驱动。
    /// </summary>
    [Fact]
    public void 文件内容变化不会让目录级缓存失效_因此只能用于估算()
    {
        using var ws = new TempWorkspace("r5-cache-stale-bytes");
        var root = BuildTree(ws);
        var cache = OpenCache(ws);

        var before = cache.EstimateTree(root);
        Assert.Equal(300, before.LogicalBytes);

        // 同名文件改写为更大的内容：目录条目没变，只有文件本身变了
        File.WriteAllText(ws.PathOf(@"Tree\a.txt"), new string('a', 9000));

        var after = cache.EstimateTree(root);

        Assert.True(after.CacheHits > 0, "预期：目录修改时间未变，缓存仍然命中（这就是局限所在）");
        Assert.Equal(300, after.LogicalBytes);                    // 过期的字节数
        Assert.Equal(9200, DirectoryScanner.Scan(root).LogicalBytes);   // 实时扫描才是真相（9000 + b.txt 的 200）

        Assert.False(after.FileCount == 0);
        Assert.NotEmpty(ScanEstimateResult.ValidityCaveat);
    }

    [Fact]
    public void 持久化_重新打开后仍能命中()
    {
        using var ws = new TempWorkspace("r5-cache-persist");
        var root = BuildTree(ws);

        var first = OpenCache(ws);
        first.EstimateTree(root);
        first.Save();
        Assert.True(File.Exists(ws.PathOf("cache.json")));

        var reopened = OpenCache(ws);
        var second = reopened.EstimateTree(root);

        Assert.True(reopened.Count > 0);
        Assert.True(second.CacheHits > 0);
        Assert.Equal(300, second.LogicalBytes);
    }

    [Fact]
    public void 禁用缓存_恒视为未命中()
    {
        using var ws = new TempWorkspace("r5-cache-disabled");
        var root = BuildTree(ws);

        var cache = OpenCache(ws, enabled: false);
        var result = cache.EstimateTree(root);

        Assert.False(cache.Enabled);
        Assert.Equal(0, result.CacheHits);
        Assert.Equal(0, cache.Count);          // 关闭时连写都不写
    }

    [Fact]
    public void 缓存文件损坏_不抛异常且视为空缓存()
    {
        using var ws = new TempWorkspace("r5-cache-corrupt");
        var root = BuildTree(ws);

        File.WriteAllText(ws.PathOf("cache.json"), "{ 这不是合法 JSON", Encoding.UTF8);

        var cache = ScanEstimateCache.Open(ws.PathOf("cache.json"));
        var result = cache.EstimateTree(root);

        Assert.Equal(0, result.CacheHits);
        Assert.Equal(300, result.LogicalBytes);    // 仍然能给出实时读到的结果
    }

    [Fact]
    public void 清空缓存后不再命中()
    {
        using var ws = new TempWorkspace("r5-cache-clear");
        var root = BuildTree(ws);

        var cache = OpenCache(ws);
        cache.EstimateTree(root);
        Assert.True(cache.Count > 0);

        cache.Clear();
        Assert.Equal(0, cache.Count);

        var after = cache.EstimateTree(root);
        Assert.Equal(0, after.CacheHits);
    }
}
