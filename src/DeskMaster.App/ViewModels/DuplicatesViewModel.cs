using static DeskMaster.App.Localization.Loc;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMaster.App.Services;
using DeskMaster.Core.Duplicates;
using DeskMaster.Core.Formatting;
using DeskMaster.Core.IO;
using DeskMaster.Core.Platform;

namespace DeskMaster.App.ViewModels;

public sealed partial class DuplicateFileViewModel(DuplicateFile file, DuplicatesViewModel owner) : ViewModelBase
{
    public DuplicateFile File { get; } = file;
    public string Path => File.Path;
    public string Name => System.IO.Path.GetFileName(File.Path);
    public string Folder => System.IO.Path.GetDirectoryName(File.Path) ?? "";
    public string ModifiedText => File.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => owner.UpdateSummary();

    [RelayCommand]
    private void Reveal() => PlatformInfo.OpenInFileManager(File.Path);
}

public sealed class DuplicateGroupViewModel : ViewModelBase
{
    public DuplicateGroupViewModel(DuplicateGroup group, DuplicatesViewModel owner)
    {
        Group = group;
        Files = new ObservableCollection<DuplicateFileViewModel>(group.Files.Select(f => new DuplicateFileViewModel(f, owner)));
    }

    public DuplicateGroup Group { get; }
    public ObservableCollection<DuplicateFileViewModel> Files { get; }
    public string Title => System.IO.Path.GetFileName(Group.Files[0].Path);
    public string Summary => F("{0} copies · {1} each · {2} wasted", Files.Count, ByteSize.Format(Group.Size), ByteSize.Format(Group.Size * (Files.Count - 1)));
    public bool AllSelected => Files.All(f => f.IsSelected);
}

/// <summary>Pro mode: find files with identical content and recycle the extra copies.</summary>
public sealed partial class DuplicatesViewModel : ViewModelBase
{
    private readonly IUiServices _ui;
    private readonly ActivityLogViewModel _log;
    private CancellationTokenSource? _cts;

    public DuplicatesViewModel(IUiServices ui, ActivityLogViewModel log)
    {
        _ui = ui;
        _log = log;
        ScanPath = UserFolders.GetPath(UserFolder.Downloads);
    }

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];
    public IReadOnlyList<int> MinSizeOptions { get; } = [1, 10, 100];

    [ObservableProperty]
    public partial string ScanPath { get; set; } = "";

    [ObservableProperty]
    public partial int MinSizeMb { get; set; } = 1;

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = T("Choose a folder or drive and press Find duplicates.");

    [ObservableProperty]
    public partial string SelectionText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _ui.PickFolderAsync(T("Choose where to look for duplicates"));
        if (path is not null)
            ScanPath = path;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning || !Directory.Exists(ScanPath))
        {
            Summary = T("That folder does not exist.");
            return;
        }

        IsScanning = true;
        _cts = new CancellationTokenSource();
        Groups.Clear();
        try
        {
            var progress = new Progress<DuplicateProgress>(p =>
                ProgressText = p.Total > 0 ? F("{0}: {1:N0} of {2:N0} files", T(p.Stage), p.Done, p.Total) : F("{0}: {1:N0} files", T(p.Stage), p.Done));
            var groups = await DuplicateFinder.FindAsync([ScanPath], new DuplicateOptions { MinSize = MinSizeMb * 1024L * 1024 }, progress, _cts.Token);
            foreach (var g in groups)
                Groups.Add(new DuplicateGroupViewModel(g, this));
            Summary = groups.Count == 0
                ? T("No duplicates found.")
                : F("{0:N0} sets of duplicates · {1} can be freed by keeping one copy of each.", groups.Count, ByteSize.Format(groups.Sum(g => g.WastedBytes)));
            _log.Info($"{ScanPath}: {Summary}");
        }
        catch (OperationCanceledException)
        {
            Summary = T("Search cancelled.");
        }
        finally
        {
            IsScanning = false;
            ProgressText = "";
            _cts.Dispose();
            _cts = null;
            UpdateSummary();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Keeps the newest copy of every set and selects the others.</summary>
    [RelayCommand]
    private void SelectAllButNewest()
    {
        foreach (var g in Groups)
        {
            var newest = g.Files.MaxBy(f => f.File.LastWriteUtc);
            foreach (var f in g.Files)
                f.IsSelected = !ReferenceEquals(f, newest);
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var f in Groups.SelectMany(g => g.Files))
            f.IsSelected = false;
    }

    [RelayCommand]
    private async Task RecycleSelectedAsync()
    {
        if (Groups.Any(g => g.AllSelected))
        {
            await _ui.AlertAsync(T("Keep at least one copy"),
                T("In some sets every copy is selected. Leave at least one copy of each file unselected."));
            return;
        }

        var selected = Groups.SelectMany(g => g.Files.Where(f => f.IsSelected).Select(f => (Group: g, File: f))).ToList();
        if (selected.Count == 0)
            return;
        var bytes = selected.Sum(s => s.Group.Group.Size);
        if (!await _ui.ConfirmAsync(T("Move duplicates to the Recycle Bin?"),
                F("{0:N0} files ({1}) go to the Recycle Bin. One copy of every file is kept.", selected.Count, ByteSize.Format(bytes)) + "\n\n" +
                T("Empty the Recycle Bin afterwards to free the space."), T("Move to Recycle Bin"), destructive: true))
            return;

        IsScanning = true;
        var failed = 0;
        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                var (group, file) = selected[i];
                ProgressText = F("Recycling {0} ({1} of {2})", file.Name, i + 1, selected.Count);
                var result = await Trash.MoveToTrashAsync(file.Path);
                if (result.Succeeded)
                {
                    group.Files.Remove(file);
                }
                else
                {
                    failed++;
                    _log.Error($"{file.Path}: {result.Message}");
                }
            }
        }
        finally
        {
            IsScanning = false;
            ProgressText = "";
        }

        foreach (var g in Groups.Where(g => g.Files.Count < 2).ToList())
            Groups.Remove(g);
        Summary = F("Moved {0:N0} duplicates to the Recycle Bin.", selected.Count - failed) + (failed > 0 ? " " + F("{0} could not be moved; see Activity.", failed) : "");
        _log.Info(Summary);
        UpdateSummary();
    }

    public void UpdateSummary()
    {
        var selected = Groups.SelectMany(g => g.Files.Where(f => f.IsSelected).Select(_ => g.Group.Size)).ToList();
        HasSelection = selected.Count > 0;
        SelectionText = HasSelection ? F("{0:N0} selected · {1}", selected.Count, ByteSize.Format(selected.Sum())) : "";
    }
}
