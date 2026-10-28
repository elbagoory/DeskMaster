using static DeskMaster.App.Localization.Loc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMaster.Core.Cleanup;
using DeskMaster.Core.Formatting;
using DeskMaster.Core.Platform;

namespace DeskMaster.App.ViewModels;

public sealed partial class FindingItemViewModel : ViewModelBase
{
    private readonly Action _selectionChanged;

    public FindingItemViewModel(RuleFinding finding, Action selectionChanged)
    {
        Finding = finding;
        _selectionChanged = selectionChanged;
    }

    public RuleFinding Finding { get; }
    public CleanupRule Rule => Finding.Rule;

    public string Name => Rule.Name;
    public string Description => Rule.Description;
    public string Category => Rule.Category;
    public string? Notes => Rule.Notes;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Rule.Notes);
    public long Bytes => Finding.Bytes;

    public string SizeText => !Finding.SizeKnown ? T("Unknown")
        : Finding.Status == FindingStatus.NothingToClean ? T("Empty")
        : ByteSize.Format(Finding.Bytes);

    public string FilesText => Rule.Handler == "wsl-compact" && Finding.Files > 0
        ? F("{0} disk images, {1} in total", Finding.Files, ByteSize.Format(Finding.Bytes))
        : Finding.SizeKnown && Finding.Files > 0 ? F("{0:N0} files", Finding.Files) : "";
    public string StatusText => Finding.StatusText;
    public bool IsReady => Finding.CanClean;
    public bool IsSafe => Rule.Safety == Safety.Safe;
    public bool IsCaution => Rule.Safety == Safety.Caution;
    public string SafetyText => IsSafe ? T("Safe") : T("Caution");
    public bool NeedsAdmin => Finding.Status == FindingStatus.NeedsAdmin;
    public bool IsBlocked => Finding.Status is FindingStatus.NeedsAdmin or FindingStatus.AppRunning;
    public bool IsEmpty => Finding.Status is FindingStatus.NothingToClean or FindingStatus.Unavailable;
    public bool RequiresAdmin => Rule.RequiresAdmin;
    public bool HasCommand => Rule.Command is not null;
    public string CommandText => Rule.Command is null ? "" : $"{Rule.Command.File} {string.Join(' ', Rule.Command.Args)}";
    public string PathsText => Finding.Paths.Count > 0 ? string.Join(Environment.NewLine, Finding.Paths) : T("No matching folders on this computer.");
    public bool HasPaths => Finding.Paths.Count > 0;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !IsReady)
        {
            IsSelected = false;
            return;
        }
        _selectionChanged();
    }

    [RelayCommand]
    private void OpenLocation()
    {
        var path = Finding.Paths.FirstOrDefault();
        if (path is not null)
            PlatformInfo.OpenInFileManager(path);
    }
}

public sealed class FindingGroupViewModel(string name, IReadOnlyList<FindingItemViewModel> items) : ViewModelBase
{
    public string Name { get; } = T(name);
    public IReadOnlyList<FindingItemViewModel> Items { get; } = items;
    public string TotalText => ByteSize.Format(Items.Where(i => i.Finding.SizeKnown).Sum(i => i.Bytes));
}
