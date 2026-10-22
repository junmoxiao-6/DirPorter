using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace DirPorter.App.Services;

/// <summary>
/// <see cref="IUserInteraction"/> 的 WPF 实现。
/// 所有调用都必须在 UI 线程（由 ViewModel 通过调度器保证）。
/// </summary>
public sealed class WpfUserInteraction : IUserInteraction
{
    private static string Title => LocalizationService.Instance.Get("msg.title");

    public void Info(string message, string? title = null)
        => MessageBox.Show(message, title ?? Title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string message, string? title = null)
        => MessageBox.Show(message, title ?? Title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string? title = null)
        => MessageBox.Show(message, title ?? Title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
           == MessageBoxResult.Yes;

    public string? PickFolder(string? initialDirectory, string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickFile(string? initialDirectory, string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Multiselect = false,
            CheckFileExists = true,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    ArgumentList = { path },
                    UseShellExecute = true,
                });
                return;
            }

            // 路径不存在时退一步打开其父目录，至少给用户一个落点。
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    ArgumentList = { parent },
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
            // 打开资源管理器失败不该中断任何流程。
        }
    }

    public void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* 剪贴板被其它进程占用时忽略 */ }
    }
}
