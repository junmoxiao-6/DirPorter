using DirPorter.App.Services;
using Xunit;

namespace DirPorter.Tests;

/// <summary>R12.7：拖动列边界时相邻两列此消彼长（方向与鼠标一致、总宽不变、各自不破下限）。</summary>
public class R16_AdjacentColumnResizeTests
{
    [Fact]
    public void 向右拖_左列变宽_右列等量变窄_总宽不变()
    {
        var (left, right) = ElasticColumns.SolveAdjacent(200, 300, +40, 60, 60);

        Assert.Equal(240, left, 1);
        Assert.Equal(260, right, 1);
        Assert.Equal(500, left + right, 1);
    }

    [Fact]
    public void 向左拖_左列变窄_右列等量变宽()
    {
        var (left, right) = ElasticColumns.SolveAdjacent(200, 300, -50, 60, 60);

        Assert.Equal(150, left, 1);
        Assert.Equal(350, right, 1);
        Assert.Equal(500, left + right, 1);
    }

    [Fact]
    public void 右列触底_停在最小宽_余量退回左列()
    {
        var (left, right) = ElasticColumns.SolveAdjacent(200, 80, +100, 60, 60);

        Assert.Equal(60, right, 1);        // 右列被下限夹住
        Assert.Equal(220, left, 1);        // 只有 20 被吃掉，其余退回
        Assert.Equal(280, left + right, 1);
    }

    [Fact]
    public void 左列触底_同样不再继续变窄()
    {
        var (left, right) = ElasticColumns.SolveAdjacent(70, 300, -100, 60, 60);

        Assert.Equal(60, left, 1);
        Assert.Equal(310, right, 1);
        Assert.Equal(370, left + right, 1);
    }
}
