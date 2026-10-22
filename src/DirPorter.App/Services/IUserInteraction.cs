namespace DirPorter.App.Services;

/// <summary>
/// 用户交互抽象（对话框 / 资源管理器 / 剪贴板）。
/// 抽成接口的目的：ViewModel 的单测里可以换成假实现，不弹窗、不开窗口。
/// </summary>
public interface IUserInteraction
{
    void Info(string message, string? title = null);

    void Error(string message, string? title = null);

    bool Confirm(string message, string? title = null);

    /// <summary>选择文件夹；取消返回 null。</summary>
    string? PickFolder(string? initialDirectory, string title);

    /// <summary>
    /// 选择文件；取消返回 null。默认实现返回 null（测试桩无需实现）——
    /// 文件级链接（硬链接 / 文件符号链接）需要它来选取已存在的目标文件。
    /// </summary>
    string? PickFile(string? initialDirectory, string title) => null;

    void OpenInExplorer(string path);

    void CopyToClipboard(string text);
}
