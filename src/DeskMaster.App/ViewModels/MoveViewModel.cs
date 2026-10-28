using static DeskMaster.App.Localization.Loc;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMaster.App.Services;
using DeskMaster.Core.Drives;
using DeskMaster.Core.Formatting;
using DeskMaster.Core.IO;
using DeskMaster.Core.Platform;
using DeskMaster.Core.Relocation;

namespace DeskMaster.App.ViewModels;

public sealed record DestinationOption(string Label, string Path);

public sealed partial class RelocationItemViewModel(RelocationCandidate candidate, MoveViewModel owner) : ViewModelBase
{
    public RelocationCandidate Candidate { get; } = candidate;
    public string Name => Candidate.Definition.Name;
    public string Description => Candidate.Definition.Description;
    public string SizeText => ByteSize.Format(Candidate.Bytes);
    public string PathText => Candidate.CurrentPath;
    public bool IsOnSystemDrive => Candidate.IsOnSystemDrive;
    public bool IsMoved => !Candidate.IsOnSystemDrive;
    public string HowText => string.IsNullOrEmpty(Candidate.Definition.EnvVar)
        ? T("You pick the new folder in the app afterwards.")
        : F("Sets {0} so the app finds it.", Candidate.Definition.EnvVar);

    [RelayCommand]
    private Task Move() => owner.MoveAsync(this);

    [RelayCommand]
    private void Reveal() => PlatformInfo.OpenInFileManager(Candidate.CurrentPath);
}

/// <summary>Moves big app folders and user folders (Downloads) off the system drive.</summary>
public sealed partial class MoveViewModel : ViewModelBase
{
    private readonly IUiServices _ui;
    private readonly ActivityLogViewModel _log;
    private readonly Func<Task> _afterMove;
    private CancellationTokenSource? _cts;

    public MoveViewModel(IUiServices ui, ActivityLogViewModel log, Func<Task> afterMove)
    {
        _ui = ui;
        _log = log;
        _afterMove = afterMove;
        CanChangeDownloadsLocation = UserFolders.CanChangeLocation;
        AlsoChangeDownloadsDefault = CanChangeDownloadsLocation;
    }

    public ObservableCollection<RelocationItemViewModel> Candidates { get; } = [];
    public ObservableCollection<RelocationItemViewModel> SystemDriveCandidates { get; } = [];
    public ObservableCollection<DestinationOption> Destinations { get; } = [];

    [ObservableProperty]
    public partial DestinationOption? SelectedDestination { get; set; }

    [ObservableProperty]
    public partial bool HasDestination { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial bool ProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial string DownloadsPath { get; set; } = "";

    [ObservableProperty]
    public partial string DownloadsSizeText { get; set; } = "";

    /// <summary>What stays behind, e.g. "4 unfinished downloads (3.57 GB) stay here".</summary>
    [ObservableProperty]
    public partial string DownloadsLeftText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasDownloadsLeft { get; set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    [ObservableProperty]
    public partial bool DownloadsOnSystemDrive { get; set; }

    [ObservableProperty]
    public partial bool HasSuggestions { get; set; }

    [ObservableProperty]
    public partial bool HasCandidates { get; set; }

    [ObservableProperty]
    public partial bool AlsoChangeDownloadsDefault { get; set; }

    public bool CanChangeDownloadsLocation { get; }

    partial void OnSelectedDestinationChanged(DestinationOption? value) => HasDestination = value is not null;

    [RelayCommand]
    public async Task DetectAsync()
    {
        RefreshDestinations();
        try
        {
            var candidates = await RelocationService.DetectAsync();
            Candidates.Clear();
            SystemDriveCandidates.Clear();
            foreach (var c in candidates)
            {
                var item = new RelocationItemViewModel(c, this);
                Candidates.Add(item);
                if (c.IsOnSystemDrive && c.Bytes >= 500L * 1024 * 1024)
                    SystemDriveCandidates.Add(item);
            }

            HasCandidates = Candidates.Count > 0;
            DownloadsPath = UserFolders.GetPath(UserFolder.Downloads);
            var plan = Directory.Exists(DownloadsPath)
                ? await Task.Run(() => FolderMover.Plan(DownloadsPath))
                : new FolderMovePlan(0, 0, 0, 0);
            DownloadsSizeText = ByteSize.Format(plan.MovableBytes);
            HasDownloadsLeft = plan.LeftBytes > 1024 * 1024;
            DownloadsLeftText = F("{0} of unfinished downloads stay here. Remove them under 'Unfinished downloads' in Cleanup.", ByteSize.Format(plan.LeftBytes));
            var drive = DriveService.DriveFor(DownloadsPath);
            DownloadsOnSystemDrive = drive?.IsSystem == true && plan.MovableBytes >= 100L * 1024 * 1024;
            HasSuggestions = SystemDriveCandidates.Count > 0 || DownloadsOnSystemDrive;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(T("Could not check movable folders:") + " " + ex.Message);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        ProgressText = T("Cancelling... the original files stay where they are.");
    }

    [RelayCommand]
    private async Task BrowseDestinationAsync()
    {
        var path = await _ui.PickFolderAsync(T("Choose where to move folders"));
        if (path is null)
            return;
        var option = new DestinationOption(path, path);
        Destinations.Add(option);
        SelectedDestination = option;
    }

    public async Task MoveAsync(RelocationItemViewModel item)
    {
        if (IsBusy || SelectedDestination is null)
            return;
        var target = Path.GetFullPath(Path.Combine(SelectedDestination.Path, item.Candidate.Definition.TargetFolderName));
        var ok = await _ui.ConfirmAsync(F("Move {0}?", item.Name),
            F("{0} will be copied to\n{1}\nand then removed from\n{2}.", item.SizeText, target, item.PathText) + "\n\n" +
            item.HowText + " " + T("Close the app that uses it before you continue."),
            T("Move"));
        if (!ok)
            return;

        IsBusy = true;
        ProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = F("Preparing to move {0}...", item.Name);
        Message = null;
        MessageIsError = false;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<CopyProgress>(p =>
            {
                ProgressValue = p.TotalBytes > 0 ? 100.0 * p.CopiedBytes / p.TotalBytes : 0;
                ProgressText = F("Copying {0}: {1} of {2}", item.Name, ByteSize.Format(p.CopiedBytes), ByteSize.Format(p.TotalBytes));
            });
            var result = await RelocationService.MoveAsync(item.Candidate, SelectedDestination.Path, progress, _cts.Token);
            Message = result.Message;
            MessageIsError = !result.Succeeded;
            if (result.Succeeded)
                _log.Info($"{item.Name}: {result.Message}");
            else
                _log.Error($"{item.Name}: {result.Message}");
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
            _cts.Dispose();
            _cts = null;
        }
        await _afterMove();
        await DetectAsync();
    }

    [RelayCommand]
    private async Task MoveDownloadsAsync()
    {
        if (IsBusy || SelectedDestination is null || !Directory.Exists(DownloadsPath))
            return;
        var target = Path.Combine(SelectedDestination.Path, "Downloads");
        var change = AlsoChangeDownloadsDefault && CanChangeDownloadsLocation;
        var ok = await _ui.ConfirmAsync(T("Move your Downloads?"),
            F("Files in {0} ({1}) move to {2}.", DownloadsPath, DownloadsSizeText, target) + "\n\n" +
            T("Nothing is overwritten: files with the same name go into a \u2018duplicates\u2019 subfolder. Unfinished downloads stay where they are.") +
            (change ? "\n\n" + F("New downloads will also be saved to {0}.", target) : ""),
            T("Move files"));
        if (!ok)
            return;

        IsBusy = true;
        ProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = T("Preparing...");
        Message = null;
        MessageIsError = false;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<FolderMoveProgress>(p =>
            {
                ProgressValue = p.TotalBytes > 0 ? 100.0 * p.CopiedBytes / p.TotalBytes : p.Total > 0 ? 100.0 * p.Done / p.Total : 100;
                ProgressText = p.Done < p.Total
                    ? F("Moving {0} ({1} of {2}) · {3} of {4}", p.CurrentName, p.Done + 1, p.Total, ByteSize.Format(p.CopiedBytes), ByteSize.Format(p.TotalBytes))
                    : T("Finishing...");
            });
            var result = await FolderMover.MoveContentsAsync(DownloadsPath, target, progress, _cts.Token);
            var moved = result.Moved + result.Duplicates;
            var summary = moved == 0 && result.Errors.Count == 0
                ? F("Nothing to move: only unfinished downloads and system files are left in {0}.", DownloadsPath)
                : F("Moved {0} items to {1}.", moved, target) +
                  (result.Duplicates > 0 ? " " + F("{0} had a name that already existed there and went to {1}.", result.Duplicates, result.DuplicatesFolder) : "") +
                  (result.Errors.Count > 0 ? " " + F("{0} could not be moved (in use?); see Activity.", result.Errors.Count) : "");
            foreach (var e in result.Errors)
                _log.Error(T("Downloads") + ": " + e);

            var failed = result.Errors.Count > 0;
            if (change)
            {
                if (UserFolders.TrySetPath(UserFolder.Downloads, target, out var note))
                {
                    summary += " " + note;
                }
                else
                {
                    summary += " " + T("Could not change where new downloads go:") + " " + note;
                    failed = true;
                }
            }

            Message = summary;
            MessageIsError = failed;
            if (failed)
                _log.Error(T("Downloads") + ": " + summary);
            else
                _log.Info(T("Downloads") + ": " + summary);
        }
        catch (OperationCanceledException)
        {
            Message = T("Move cancelled. Files already moved are in the new folder; the rest were not changed.");
            MessageIsError = true;
            _log.Info(T("Downloads") + ": " + Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = T("Moving Downloads failed:") + " " + ex.Message;
            MessageIsError = true;
            _log.Error(Message);
        }
        finally
        {
            IsBusy = false;
            ProgressIndeterminate = false;
            ProgressText = "";
            _cts.Dispose();
            _cts = null;
        }
        await _afterMove();
        await DetectAsync();
    }

    private void RefreshDestinations()
    {
        var keep = SelectedDestination?.Path;
        var custom = Destinations.Where(d => !DriveService.GetDrives().Any(x => x.RootPath == d.Path)).ToList();
        Destinations.Clear();
        foreach (var d in DriveService.GetDrives().Where(d => !d.IsSystem && !d.IsRemovable))
            Destinations.Add(new DestinationOption(F("{0} — {1} free", d.DisplayName, ByteSize.Format(d.FreeBytes)), d.RootPath));
        foreach (var c in custom)
            Destinations.Add(c);
        SelectedDestination = Destinations.FirstOrDefault(d => d.Path == keep) ?? Destinations.FirstOrDefault();
    }
}
