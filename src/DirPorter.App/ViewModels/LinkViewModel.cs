using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirPorter.App.Services;
using DirPorter.Core.FileSystem;
using DirPorter.Core.Links;

namespace DirPorter.App.ViewModels;

/// <summary>能力矩阵中的一行（四种链接类型之一，含"自动推荐"）。</summary>
public sealed partial class LinkOptionRow : ObservableObject
{
    public LinkOptionRow(LinkKind? kind, string nameKey)
    {
        Kind = kind;
        NameKey = nameKey;
        Refresh();
    }

    /// <summary>null 表示自动推荐。</summary>
    public LinkKind? Kind { get; }

    public string NameKey { get; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _canHandle;
    [ObservableProperty] private string _reason = string.Empty;
    [ObservableProperty] private string _suggestion = string.Empty;

    public void Refresh() => Name = LocalizationService.Instance.Get(NameKey);
}

/// <summary>已存在的链接（用于直接删链接）。</summary>
public sealed partial class ExistingLinkRow : ObservableObject, IPathRow
{
    public ExistingLinkRow(string linkPath, string targetPath, string kindText, string healthText)
    {
        LinkPath = linkPath;
        TargetPath = targetPath;
        KindText = kindText;
        HealthText = healthText;
    }

    /// <summary>双击整行时打开链接所在位置。</summary>
    public string PrimaryPath => LinkPath;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _linkPath;
    [ObservableProperty] private string _targetPath;
    [ObservableProperty] private string _kindText;
    [ObservableProperty] private string _healthText;
}

/// <summary>
/// 链接页 ViewModel（需求「四种 mklink 模式全覆盖」+「能力矩阵置灰并写明原因」）。
///
/// 能力矩阵来自 <see cref="LinkService.EvaluateAll"/>，与 CLI 的 <c>link --dry-run</c> 同一套判定，
/// 因此界面上"能不能点"与命令行"能不能建"永远一致。
/// </summary>
public sealed partial class LinkViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUserInteraction _ui;
    private readonly UiDebouncer _evalDebouncer = new(TimeSpan.FromMilliseconds(300));
    private bool _loaded;

    /// <summary>R12 启动性能：本页数据改为**首次进入才加载**（构造期不再读台账 + 逐条重解析点探测）。</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        ReloadExistingLinks();
    }

    public LinkViewModel(AppHost host)
    {
        _host = host;
        _ui = host.Interaction;

        Options = new ObservableCollection<LinkOptionRow>
        {
            new(null, "link.type.auto"),
            new(LinkKind.Junction, "link.type.junction"),
            new(LinkKind.DirectorySymlink, "link.type.symlinkDir"),
            new(LinkKind.FileSymlink, "link.type.symlinkFile"),
            new(LinkKind.HardLink, "link.type.hardlink"),
        };

        _selectedOption = Options[0];
        _resultText = string.Empty;
        // 空状态与表格互斥显示（R11）：集合变化即通知。
        ExistingLinks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasExistingLinks));
        Evaluate();   // 空输入的初始渲染（轻量）
    }

    public ObservableCollection<LinkOptionRow> Options { get; }

    public ObservableCollection<ExistingLinkRow> ExistingLinks { get; } = new();

    /// <summary>是否有已存在的链接（驱动空状态提示）。</summary>
    public bool HasExistingLinks => ExistingLinks.Count > 0;

    [ObservableProperty] private LinkOptionRow _selectedOption;
    [ObservableProperty] private string _linkPath = string.Empty;
    [ObservableProperty] private string _targetPath = string.Empty;
    [ObservableProperty] private bool _useRelative;
    [ObservableProperty] private string _recommendText = string.Empty;
    [ObservableProperty] private string _resultText;
    [ObservableProperty] private string _privilegeText = string.Empty;

    // R12 性能修复：输入防抖——不再每敲一键跑一轮文件系统探测风暴（4 个 provider × 多次卷/目录 IO）。
    partial void OnLinkPathChanged(string value) => _evalDebouncer.Poke(Evaluate);

    partial void OnTargetPathChanged(string value) => _evalDebouncer.Poke(Evaluate);

    partial void OnSelectedOptionChanged(LinkOptionRow value) => _evalDebouncer.Poke(Evaluate);

    /// <summary>
    /// 重新计算能力矩阵。任一入参为空时矩阵显示"待输入"，而不是假"可创建"。
    /// </summary>
    [RelayCommand]
    private void Evaluate()
    {
        try
        {
            EvaluateCore();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 输入不合法（如粘贴带引号的路径）只应在页面上提示，绝不能让异常冒到 UI 线程之外。
            var message = LocalizationService.Instance.Get("link.invalidPath");

            foreach (var option in Options)
            {
                option.CanHandle = false;
                option.Reason = message;
                option.Suggestion = string.Empty;
            }

            RecommendText = message + " " + ex.Message;
        }
    }

    private void EvaluateCore()
    {
        var linkPath = PathNormalizer.CleanInput(LinkPath);
        var targetPath = PathNormalizer.CleanInput(TargetPath);

        PrivilegeText = _host.Privileges.CanCreateSymbolicLink
            ? LocalizationService.Instance.Get("priv.full")
            : LocalizationService.Instance.Get("priv.limited");

        if (linkPath.Length == 0 || targetPath.Length == 0)
        {
            foreach (var option in Options)
            {
                option.CanHandle = false;
                option.Reason = LocalizationService.Instance.Get("common.none");
                option.Suggestion = string.Empty;
            }

            RecommendText = string.Empty;
            return;
        }

        var request = new LinkRequest(linkPath, targetPath, SelectedOption.Kind, UseRelative);

        // 自动推荐的理由
        var recommendation = _host.Links.Recommend(request);
        RecommendText = LocalizationService.Instance.Get("link.recommend")
                        + NameOf(recommendation.Kind) + " —— " + recommendation.Reason;

        var resolutions = _host.Links.EvaluateAll(request);

        foreach (var option in Options)
        {
            var targetKind = option.Kind ?? recommendation.Kind;
            var resolution = resolutions.FirstOrDefault(r => r.Kind == targetKind);

            if (resolution is null)
            {
                option.CanHandle = false;
                option.Reason = LocalizationService.Instance.Get("common.none");
                option.Suggestion = string.Empty;
                continue;
            }

            option.CanHandle = resolution.Capability.CanHandle;
            option.Reason = resolution.Capability.Reason;
            option.Suggestion = resolution.Capability.Suggestion ?? string.Empty;
        }
    }

    private static string NameOf(LinkKind kind) => kind switch
    {
        LinkKind.Junction => LocalizationService.Instance.Get("link.type.junction"),
        LinkKind.DirectorySymlink => LocalizationService.Instance.Get("link.type.symlinkDir"),
        LinkKind.FileSymlink => LocalizationService.Instance.Get("link.type.symlinkFile"),
        _ => LocalizationService.Instance.Get("link.type.hardlink"),
    };

    [RelayCommand]
    private void BrowseLinkPath()
    {
        var picked = _ui.PickFolder(null, LocalizationService.Instance.Get("link.linkPath"));
        if (picked is not null) LinkPath = picked;
    }

    [RelayCommand]
    private void BrowseTargetPath()
    {
        var picked = _ui.PickFolder(null, LocalizationService.Instance.Get("link.targetPath"));
        if (picked is not null) TargetPath = picked;
    }

    /// <summary>选择**文件**作为目标（R12：文件符号链接 / 硬链接的目标必须是文件）。</summary>
    [RelayCommand]
    private void BrowseTargetFile()
    {
        var picked = _ui.PickFile(null, LocalizationService.Instance.Get("link.targetPath"));
        if (picked is not null) TargetPath = picked;
    }

    [RelayCommand]
    private void Create()
    {
        CreateCore(dryRun: false);
    }

    [RelayCommand]
    private void DryRunCreate() => CreateCore(dryRun: true);

    private void CreateCore(bool dryRun)
    {
        var linkPath = PathNormalizer.CleanInput(LinkPath);
        var targetPath = PathNormalizer.CleanInput(TargetPath);

        if (linkPath.Length == 0 || targetPath.Length == 0)
        {
            _ui.Error(LocalizationService.Instance.Get("common.none"));
            return;
        }

        var request = new LinkRequest(linkPath, targetPath, SelectedOption.Kind, UseRelative);

        try
        {
            var resolution = _host.Links.Resolve(request);

            if (!resolution.Capability.CanHandle)
            {
                var loc = LocalizationService.Instance;
                var message = resolution.Capability.Reason
                              + (string.IsNullOrWhiteSpace(resolution.Capability.Suggestion)
                                  ? string.Empty
                                  : Environment.NewLine + resolution.Capability.Suggestion);

                // 只说"不支持"没用 —— 用户下一步该做什么必须摆出来：
                // ① 当前输入下哪种类型可用；② 不想选就切回「自动推荐」让它替你决定。
                var usable = Options
                    .Where(o => o.CanHandle && o.Kind is not null)
                    .Select(o => NameOf(o.Kind!.Value))
                    .ToList();

                if (usable.Count > 0)
                {
                    message += Environment.NewLine
                               + string.Format(System.Globalization.CultureInfo.CurrentCulture,
                                   loc.Get("link.usableKinds"), string.Join(" / ", usable));
                }

                var auto = Options.FirstOrDefault(o => o.Kind is null && o.CanHandle);
                if (auto is not null)
                {
                    message += Environment.NewLine + loc.Get("link.useAutoHint");
                }

                ResultText = message;
                _ui.Error(message);
                return;
            }

            // Create 返回解析结果（含实际选用的类型）；展示用 LinkInfo 需要再检视一次。
            _host.Links.Create(request, new LinkCreateOptions
            {
                DryRun = dryRun,
                Relative = UseRelative,
            });

            var info = _host.Links.Inspect(linkPath);
            ResultText = (dryRun ? "[干跑] " : string.Empty) + info.Describe();
            if (!dryRun) _ui.Info(string.Format(LocalizationService.Instance.Get("link.created"), ResultText));

            Evaluate();
            ReloadExistingLinks();
        }
        catch (LinkPrivilegeRequiredException)
        {
            ResultText = LocalizationService.Instance.Get("link.needPrivilege");

            // 只报"需要管理员权限"没有用 —— 直接把提权重启摆到用户面前。
            if (ElevationPrompt.Offer(_host, _ui, ResultText))
                System.Windows.Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            ResultText = string.Format(LocalizationService.Instance.Get("link.createFailed"), ex.Message);
            _ui.Error(ResultText);
        }
    }

    [RelayCommand]
    private void Inspect()
    {
        var linkPath = PathNormalizer.CleanInput(LinkPath);
        if (linkPath.Length == 0) return;

        try
        {
            ResultText = _host.Links.Inspect(linkPath).Describe();
        }
        catch (Exception ex)
        {
            ResultText = ex.Message;
        }
    }

    /// <summary>删除链接（只删链接本体，绝不递归目标数据）。</summary>
    [RelayCommand]
    private void Unlink() => UnlinkCore(PathNormalizer.CleanInput(LinkPath));

    [RelayCommand]
    private void UnlinkRow(ExistingLinkRow? row)
    {
        if (row is null) return;
        if (UnlinkCore(row.LinkPath)) ExistingLinks.Remove(row);
    }

    private bool UnlinkCore(string linkPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath)) return false;

        var info = _host.Links.Inspect(linkPath);

        if (!info.IsKnownLink)
        {
            ResultText = info.PathExists
                ? LocalizationService.Instance.Get("hist.health.notLink")
                : LocalizationService.Instance.Get("hist.health.missing");
            _ui.Error(ResultText);
            return false;
        }

        if (!_ui.Confirm(LocalizationService.Instance.Get("link.unlinkConfirm"))) return false;

        try
        {
            var kind = _host.Links.Delete(linkPath);
            ResultText = $"{NameOf(kind)}：{LocalizationService.Instance.Get("link.unlinkSafe")}";
            Evaluate();
            ReloadExistingLinks();
            return true;
        }
        catch (LinkDeleteRefusedException ex)
        {
            // 保守拒绝（如硬链接数已降回 1）——如实告知，不尝试绕过。
            ResultText = ex.Message;
            _ui.Error(ResultText);
            return false;
        }
        catch (Exception ex)
        {
            ResultText = ex.Message;
            _ui.Error(ResultText);
            return false;
        }
    }

    /// <summary>从迁移记录里找出当前仍是链接的条目，便于直接删除。</summary>
    [RelayCommand]
    private void ReloadExistingLinks()
    {
        _loaded = true;
        ExistingLinks.Clear();

        foreach (var record in _host.Index.LoadAll())
        {
            if (string.IsNullOrWhiteSpace(record.SourcePath)) continue;

            LinkInfo info;
            try { info = _host.Links.Inspect(record.SourcePath); }
            catch { continue; }

            if (!info.IsKnownLink) continue;

            ExistingLinks.Add(new ExistingLinkRow(
                record.SourcePath,
                info.TargetAbsolute ?? LocalizationService.Instance.Get("common.dash"),
                NameOf(info.Kind!.Value),
                info.IsDangling
                    ? LocalizationService.Instance.Get("hist.health.dangling")
                    : LocalizationService.Instance.Get("hist.health.healthy")));
        }
    }

    public void RefreshLocalization()
    {
        foreach (var option in Options) option.Refresh();

        Evaluate();
        ReloadExistingLinks();
    }
}
