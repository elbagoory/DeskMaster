using static DeskMaster.App.Localization.Loc;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMaster.App.Services;
using DeskMaster.Core.Drives;
using DeskMaster.Core.Formatting;
using DeskMaster.Core.Platform;
using DeskMaster.Core.Scanning;

namespace DeskMaster.App.ViewModels;

public sealed record ScanTarget(string Label, string Path);

public sealed partial class NodeItemViewModel(ScanNode node, long parentSize, ExplorerViewModel owner) : ViewModelBase
{
    public ScanNode Node { get; } = node;
    public string Name => Node.Name;
    public string FullPath => Node.FullPath;
    public string SizeText => ByteSize.Format(Node.Size);
    public double Percent => parentSize > 0 ? 100.0 * Node.Size / parentSize : 0;
    public string PercentText => ByteSize.Percent(parentSize > 0 ? (double)Node.Size / parentSize : 0);
    public string FilesText => Node.IsDirectory || Node.Kind == ScanNodeKind.SmallFiles ? $"{Node.FileCount:N0}" : "";
    public string ModifiedText => Node.LastWriteUtc == default ? "" : Node.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd");
    public bool IsDirectory => Node.IsDirectory;
    public bool IsFile => Node.Kind == ScanNodeKind.File;
    public bool IsGroup => Node.Kind == ScanNodeKind.SmallFiles;
    public bool CanOpen => Node.IsDirectory && Node.Children.Count > 0;
    public bool Incomplete => Node.Incomplete;
    public bool CanDelete => Node.Kind != ScanNodeKind.SmallFiles;

    [RelayCommand]
    private Task Recycle() => owner.DeleteAsync(Node, permanently: false);

    [RelayCommand]
    private Task DeletePermanently() => owner.DeleteAsync(Node, permanently: true);

    [RelayCommand]
    private void Open()
    {
        if (CanOpen)
            owner.NavigateTo(Node);
    }

    [RelayCommand]
    private void Reveal() => PlatformInfo.OpenInFileManager(Node.FullPath);
}

public sealed record Breadcrumb(string Name, ScanNode Node, IRelayCommand Go);

/// <summary>Pro mode: scan a drive or folder and drill into what uses the space.</summary>
public sealed partial class ExplorerViewModel : ViewModelBase
{
    private readonly IUiServices _ui;
    private readonly ActivityLogViewModel _log;
    private CancellationTokenSource? _cts;

    public ExplorerViewModel(IUiServices ui, ActivityLogViewModel log)
    {
        _ui = ui;
        _log = log;
        Sort = SortOptions[0];
        RefreshTargets();
    }

    public ObservableCollection<ScanTarget> Targets { get; } = [];
    public ObservableCollection<NodeItemViewModel> Children { get; } = [];
    public ObservableCollection<NodeItemViewModel> LargestFiles { get; } = [];
    public ObservableCollection<Breadcrumb> Breadcrumbs { get; } = [];
    public IReadOnlyList<Choice> SortOptions { get; } =
        [new("Size", T("Size")), new("Name", T("Name")), new("Files", T("Files")), new("Modified", T("Modified"))];

    [ObservableProperty]
    public partial ScanTarget? SelectedTarget { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = T("Pick a drive or folder and press Scan.");

    [ObservableProperty]
    public partial bool HasScan { get; set; }

    [ObservableProperty]
    public partial ScanNode? Current { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ScanNode>? TreemapItems { get; set; }

    [ObservableProperty]
    public partial string CurrentSummary { get; set; } = "";

    [ObservableProperty]
    public partial bool CanGoUp { get; set; }

    [ObservableProperty]
    public partial Choice? Sort { get; set; }

    private string SortBy => Sort?.Value ?? "Size";

    partial void OnSortChanged(Choice? value) => ShowChildren();

    public void RefreshTargets()
    {
        var keep = SelectedTarget?.Path;
        Targets.Clear();
        foreach (var d in DriveService.GetDrives())
            Targets.Add(new ScanTarget(F("{0} — {1} used", d.DisplayName, ByteSize.Format(d.UsedBytes)), d.RootPath));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Targets.Add(new ScanTarget(F("Home folder ({0})", home), home));
        SelectedTarget = Targets.FirstOrDefault(t => t.Path == keep) ?? Targets.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (SelectedTarget is { } target)
            await ScanPathAsync(target.Path);
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var path = await _ui.PickFolderAsync(T("Choose a folder to scan"));
        if (path is null)
            return;
        var target = new ScanTarget(path, path);
        Targets.Add(target);
        SelectedTarget = target;
        await ScanPathAsync(path);
    }

    public async Task ScanPathAsync(string path)
    {
        if (IsScanning)
            return;
        _cts = new CancellationTokenSource();
        IsScanning = true;
        ProgressText = T("Starting...");
        try
        {
            var progress = new Progress<ScanProgress>(p =>
                ProgressText = F("{0:N0} files · {1} · {2}", p.Files, ByteSize.Format(p.Bytes), p.CurrentPath));
            var result = await DiskScanner.ScanAsync(path, progress, null, _cts.Token);

            LargestFiles.Clear();
            foreach (var f in result.LargestFiles)
                LargestFiles.Add(new NodeItemViewModel(f, result.Root.Size, this));

            HasScan = true;
            StatusText = F("Scanned {0} in {1:N0} files in {2:0}s", ByteSize.Format(result.Root.Size), result.Root.FileCount, result.Duration.TotalSeconds) +
                         (result.Errors > 0 ? " · " + F("{0:N0} folders could not be read", result.Errors) +
                                              (PlatformInfo.IsElevated ? "" : " " + T("(run as administrator to include them)")) : "");
            _log.Info(F("Scanned {0}: {1}.", path, ByteSize.Format(result.Root.Size)));
            NavigateTo(result.Root);
        }
        catch (OperationCanceledException)
        {
            StatusText = T("Scan cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = T("Scan failed:") + " " + ex.Message;
            _log.Error(StatusText);
        }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Up()
    {
        if (Current?.Parent is { } parent)
            NavigateTo(parent);
    }

    [RelayCommand]
    private void OpenNode(ScanNode? node)
    {
        if (node is { IsDirectory: true, Children.Count: > 0 })
            NavigateTo(node);
    }

    [RelayCommand]
    private void RevealCurrent()
    {
        if (Current is not null)
            PlatformInfo.OpenInFileManager(Current.FullPath);
    }

    public async Task DeleteAsync(ScanNode node, bool permanently)
    {
        if (node.Kind == ScanNodeKind.SmallFiles)
            return;
        if (!SafetyGuard.IsSafeToDelete(node.FullPath, out var reason))
        {
            await _ui.AlertAsync(T("Can't delete this"), $"{node.FullPath}\n\n{reason}");
            return;
        }

        var what = node.IsDirectory
            ? F("the folder {0} ({1}, {2:N0} files)", node.Name, ByteSize.Format(node.Size), node.FileCount)
            : $"{node.Name} ({ByteSize.Format(node.Size)})";
        var bin = PlatformInfo.Os == OsKind.Windows ? T("Recycle Bin") : T("Trash");
        var ok = permanently
            ? await _ui.ConfirmAsync(T("Delete permanently?"),
                F("Permanently delete {0}?", what) + $"\n\n{node.FullPath}\n\n" + T("This cannot be undone."), T("Delete permanently"), destructive: true)
            : await _ui.ConfirmAsync(T("Move to Recycle Bin?"),
                F("Move {0} to the {1}?", what, bin) + $"\n\n{node.FullPath}\n\n" +
                T("You can restore it from there. The space is freed when you empty it."), T("Move to Recycle Bin"), destructive: true);
        if (!ok)
            return;

        var result = permanently
            ? await DeskMaster.Core.IO.Trash.DeletePermanentlyAsync(node.FullPath)
            : await DeskMaster.Core.IO.Trash.MoveToTrashAsync(node.FullPath);
        if (!result.Succeeded)
        {
            _log.Error($"{node.FullPath}: {result.Message}");
            StatusText = F("Could not delete {0}: {1}", node.Name, result.Message);
            return;
        }

        _log.Info($"{node.FullPath}: {result.Message}");
        StatusText = $"{node.Name}: {result.Message}";
        node.Detach();
        foreach (var gone in LargestFiles.Where(f => ReferenceEquals(f.Node, node) || f.Node.Ancestors().Contains(node)).ToList())
            LargestFiles.Remove(gone);
        if (Current is not null && (ReferenceEquals(Current, node) || Current.Ancestors().Contains(node)))
            NavigateTo(node.Parent ?? Current);
        else if (Current is not null)
            NavigateTo(Current);
    }

    public void NavigateTo(ScanNode node)
    {
        Current = node;
        CanGoUp = node.Parent is not null;
        CurrentSummary = F("{0} · {1:N0} files", ByteSize.Format(node.Size), node.FileCount);

        Breadcrumbs.Clear();
        foreach (var n in node.Ancestors().Reverse().Append(node))
            Breadcrumbs.Add(new Breadcrumb(n.Name, n, new RelayCommand(() => NavigateTo(n))));

        TreemapItems = node.Children;
        ShowChildren();
    }

    private void ShowChildren()
    {
        Children.Clear();
        if (Current is null)
            return;
        IEnumerable<ScanNode> items = SortBy switch
        {
            "Name" => Current.Children.OrderByDescending(c => c.IsDirectory).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            "Files" => Current.Children.OrderByDescending(c => c.FileCount),
            "Modified" => Current.Children.OrderByDescending(c => c.LastWriteUtc),
            _ => Current.Children,
        };
        foreach (var child in items)
            Children.Add(new NodeItemViewModel(child, Current.Size, this));
    }
}
