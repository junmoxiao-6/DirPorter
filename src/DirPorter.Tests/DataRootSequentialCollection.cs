using Xunit;

namespace DirPorter.Tests;

/// <summary>
/// 会读写**进程级共享数据根**（`migrations.json` / `runs/` / 台账）的用例集合。
///
/// ⚠️ 为什么必须串行：<c>DirPorterPaths</c> 是进程级静态的，所有测试共用一个数据根。
/// 实测踩到的坑：R8 的"一键迁移并建链"端到端用例会写入迁移记录，而 R6 的
/// <c>verify_无记录时成功返回</c> 会读同一份记录 —— 两者并行时，R6 读到了 R8 刚写入的
/// "源是链接但目标已被清理"的记录，于是 `verify` 返回 1，用例随机失败。
///
/// 这类"共享可变全局状态"的用例必须串行，否则失败与否取决于调度顺序（最糟糕的一种不稳定）。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class DataRootSequentialCollection
{
    public const string Name = "DataRootSequential";
}
