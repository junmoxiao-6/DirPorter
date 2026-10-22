using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirPorter.App.Services;
using DirPorter.Core.Links;

namespace DirPorter.App.ViewModels;

/// <summary>
/// R12.5：链接总览——列出扫描范围内所有链接（目录联接 / 目录符号链接 / 文件符号链接，可选硬链接），
/// 显示链接所在路径、类型、目标（源目录），并支持"还原"（把数据搬回链接位置并移除链接）。
/// </summary>
public sealed partial class LinkOverviewViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUserInteraction _ui;

    public LinkOverviewViewModel(AppHost host)
    {
        _host = host;
        _ui = host.Interaction;

        Roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _status = LocalizationService.Instance.Get("lov.idle");
    }

    public ObservableCollection<ScannedLink> Links { get; } = new();

    public ObservableCollection<string> Roots { get; } = new();

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _includeHardLinks;
    [ObservableProperty] private string _status;

    public bool HasLinks => Links.Count > 0;

    public bool CanScan => !IsScanning;

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(CanScan));

    [RelayCommand]
    private void AddRoot()
    {
        var picked = _ui.PickFolder(null, LocalizationService.Instance.Get("lov.addRoot"));
        if (string.IsNullOrWhiteSpace(picked)) return;
        if (Roots.Contains(picked, StringComparer.OrdinalIgnoreCase)) return;

        Roots.Add(picked);
    }

    [RelayCommand]
    private void RemoveRoot(string? root)
    {
        if (root is null) return;
        Roots.Remove(root);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;

        var loc = LocalizationService.Instance;
        IsScanning = true;
        Status = loc.Get("lov.scanning");

        try
        {
            var options = new LinkScanner.Options(IncludeHardLinks);
            var roots = Roots.ToList();
            var found = await Task.Run(() => LinkScanner.Scan(roots, _host.Links, options)).ConfigureAwait(true);

            Links.Clear();
            foreach (var link in found) Links.Add(link);

            OnPropertyChanged(nameof(HasLinks));
            Status = string.Format(System.Globalization.CultureInfo.CurrentCulture, loc.Get("lov.done"), Links.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = loc.Get("lov.failed") + " " + ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void OpenLink(ScannedLink? link)
    {
        if (link is null) return;
        _ui.OpenInExplorer(link.LinkPath);
    }

    [RelayCommand]
    private void RestoreLink(ScannedLink? link)
    {
        if (link is null) return;

        var loc = LocalizationService.Instance;

        if (link.Kind is not (LinkKind.Junction or LinkKind.DirectorySymlink))
        {
            _ui.Error(loc.Get("lov.restoreUnsupported"));
            return;
        }

        if (!_ui.Confirm(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, loc.Get("lov.restoreConfirm"), link.LinkPath, link.TargetPath ?? "?"),
                loc.Get("lov.restore")))
        {
            return;
        }

        var result = LinkRestorer.Restore(link.LinkPath, _host.Links);
        if (result.Success)
        {
            Links.Remove(link);
            OnPropertyChanged(nameof(HasLinks));
            _ui.Info(result.Message);
            Status = result.Message;
        }
        else
        {
            _ui.Error(result.Message);
        }
    }
}
