namespace DirPorter.Core.Links;

/// <summary>
/// 统一的链接提供器抽象（需求「链接类型支持」架构要求）。
/// 四个实现：<see cref="JunctionLinkProvider"/> / <see cref="DirSymlinkLinkProvider"/>
/// / <see cref="FileSymlinkLinkProvider"/> / <see cref="HardLinkProvider"/>。
///
/// 迁移状态机的 LINKING / LINK_VERIFY / restore 全部经由本接口执行，
/// 因此三种目录级链接（Junction、目录符号链接）都可用于迁移场景。
/// </summary>
public interface ILinkProvider
{
    /// <summary>本提供器对应的链接类型。</summary>
    LinkKind Kind { get; }

    /// <summary>是否支持以目录作为操作对象。</summary>
    bool SupportsDirectories { get; }

    /// <summary>是否支持以文件作为操作对象。</summary>
    bool SupportsFiles { get; }

    /// <summary>
    /// 能力校验：对象类型 / 卷 / 文件系统 / 权限。富信息版本，供 UI 置灰与 CLI --json 使用。
    /// 每一个拒绝码都必须有对应的单元测试用例。
    /// </summary>
    LinkCapability Evaluate(LinkRequest req);

    /// <summary>
    /// 能力校验的布尔版本（需求原始签名）。不通过时 <paramref name="reason"/> 为人类可读原因。
    /// </summary>
    bool CanHandle(LinkRequest req, out string reason);

    /// <summary>
    /// 创建链接。
    /// 抛出：<see cref="LinkPrivilegeRequiredException"/>（需提权）、
    /// <see cref="InvalidOperationException"/>（能力校验不通过）、
    /// <see cref="DirPorter.Core.Diagnostics.DirPorterNativeException"/>（原生调用失败）。
    /// </summary>
    void Create(LinkRequest req, LinkCreateOptions opt);

    /// <summary>链接本体是否存在（悬空链接同样返回 true）。</summary>
    bool Exists(string path);

    /// <summary>链接类型、重解析标签、目标绝对路径、是否相对、是否悬空。</summary>
    LinkInfo Inspect(string path);

    /// <summary>
    /// 只删除链接本体，绝不触碰目标数据。
    /// ⚠️ 实现必须遵守安全红线：任何情况下不得递归删除（详见 FileSystem.ReparsePointOps.RemoveLinkBody）。
    /// </summary>
    void Delete(string linkPath);
}
