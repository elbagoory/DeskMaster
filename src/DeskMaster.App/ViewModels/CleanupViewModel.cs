using static DeskMaster.App.Localization.Loc;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMaster.App.Services;
using DeskMaster.Core.Cleanup;
using DeskMaster.Core.Formatting;

namespace DeskMaster.App.ViewModels;

/// <summary>Measures cleanup rules and cleans the ones the user picks.</summary>
public sealed partial class CleanupViewModel : ViewModelBase
{
    private readonly IUiServices _ui;
    private readonly ActivityLogViewModel _log;
    private readonly Func<Task> _afterClean;
    private List<FindingItemViewModel> _all = [];
    private CancellationTokenSource? _cleanCts;

    public CleanupViewModel(IUiServices ui, ActivityLogViewModel log, Func<Task> afterClean)
    {
        _ui = ui;
        _log = log;
        _afterClean = afterClean;
    }

    /// <summary>Items worth showing in Simple mode: ready ones first, then blocked ones.</summary>
    public ObservableCollection<FindingItemViewModel> SimpleItems { get; } = [];

    /// <summary>Every rule for this OS, grouped by category (Pro mode).</summary>
    public ObservableCollection<FindingGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial bool HasResults { get; set; }

    [ObservableProperty]
    public partial string SelectedSizeText { get; set; } = "0 B";

    [ObservableProperty]
    public partial string SelectedCountText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [ObservableProperty]
    public partial string? ResultMessage { get; set; }

    [ObservableProperty]
    public partial string AdminSummary { get; set; } = "";

    [ObservableProperty]
    public partial bool HasAdminItems { get; set; }

    [ObservableProperty]
    public partial bool ShowEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsCleaning { get; set; }

    partial void OnShowEmptyChanged(bool value) => RebuildGroups();

    [RelayCommand]
    public async Task AnalyzeAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        ProgressValue = 0;
        ProgressText = T("Checking your computer...");
        try
        {
            var warnings = new List<string>();
            var rules = RuleCatalog.LoadForCurrentOs(warnings);
            foreach (var w in warnings)
                _log.Error(w);

            var progress = new Progress<AnalysisProgress>(p =>
            {
                ProgressValue = 100.0 * p.Completed / Math.Max(1, p.Total);
                ProgressText = F("Checking {0}...", p.RuleName);
            });
            var findings = await RuleAnalyzer.AnalyzeAsync(rules, progress);

            var previouslySelected = _all.Where(i => i.IsSelected).Select(i => i.Rule.Id).ToHashSet();
            var firstRun = _all.Count == 0;
            _all = findings
                .Select(f => new FindingItemViewModel(f, UpdateSelection))
                .OrderByDescending(i => i.IsReady)
                .ThenByDescending(i => i.Bytes)
                .ToList();

            foreach (var item in _all.Where(i => i.IsReady))
                item.IsSelected = firstRun || previouslySelected.Count == 0 ? item.Rule.IsRecommended : previouslySelected.Contains(item.Rule.Id);

            SimpleItems.Clear();
            foreach (var item in _all.Where(i => !i.IsEmpty && !i.NeedsAdmin && (i.Finding.SizeKnown ? i.Bytes > 0 : i.IsReady)))
                SimpleItems.Add(item);
            RebuildGroups();

            var admin = _all.Where(i => i.NeedsAdmin).ToList();
            HasAdminItems = admin.Count > 0;
            AdminSummary = F(admin.Count == 1 ? "1 more item ({1}) needs administrator rights." : "{0} more items ({1}) need administrator rights.", admin.Count, ByteSize.Format(admin.Sum(i => i.Bytes)));
            HasResults = true;
            UpdateSelection();
            _log.Info(F("Analysis finished: {0} can be cleaned now.", ByteSize.Format(_all.Where(i => i.IsReady).Sum(i => i.Bytes))));
        }
        catch (Exception ex)
        {
            _log.Error(T("Analysis failed:") + " " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
        }
    }

    [RelayCommand]
    private async Task CleanSelectedAsync()
    {
        var selected = _all.Where(i => i.IsSelected && i.IsReady).ToList();
        if (selected.Count == 0 || IsBusy)
            return;

        var total = selected.Where(i => i.Finding.SizeKnown).Sum(i => i.Bytes);
        var caution = selected.Where(i => i.IsCaution).Select(i => "• " + i.Name + ": " + i.Description).ToList();
        var message = F(selected.Count == 1 ? "DeskMaster will permanently delete about {0} from 1 item. Files that are in use are skipped." : "DeskMaster will permanently delete about {0} from {1} items. Files that are in use are skipped.", ByteSize.Format(total), selected.Count);
        if (caution.Count > 0)
            message += "\n\n" + T("Please note:") + "\n" + string.Join("\n", caution);
        if (!await _ui.ConfirmAsync(T("Clean selected items?"), message, T("Clean now"), caution.Count > 0))
            return;

        IsBusy = true;
        IsCleaning = true;
        _cleanCts = new CancellationTokenSource();
        var ct = _cleanCts.Token;
        ResultMessage = null;
        long freed = 0;
        var failures = 0;
        var unknown = false;
        var cancelled = false;
        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                var item = selected[i];
                ProgressValue = 100.0 * i / selected.Count;
                ProgressText = F("Cleaning {0} ({1} of {2})...", item.Name, i + 1, selected.Count);
                var progress = new Progress<CleanProgress>(p =>
                    ProgressText = F("Cleaning {0}: {1:N0} files, {2}", item.Name, p.DeletedFiles, ByteSize.Format(p.FreedBytes)));

                CleanResult result;
                try
                {
                    result = await Cleaner.CleanAsync(item.Finding, progress, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    _log.Info(item.Name + ": " + T("cancelled. Files already deleted stay deleted; nothing else was touched."));
                    break;
                }
                catch (Exception ex)
                {
                    failures++;
                    _log.Error($"{item.Name}: {ex.Message}");
                    continue;
                }

                freed += result.FreedBytes;
                unknown |= !result.FreedKnown;
                if (result.Succeeded)
                {
                    _log.Info(item.Name + ": " + (result.FreedKnown ? F("freed {0}.", ByteSize.Format(result.FreedBytes)) : T("freed an unknown amount.")) + " " + result.Message);
                }
                else
                {
                    failures++;
                    _log.Error($"{item.Name}: {result.Message}");
                }
                foreach (var error in result.Errors)
                    _log.Error($"  {error}");
            }

            ResultMessage = (cancelled ? T("Cancelled.") + " " : "") + F("Freed {0}.", ByteSize.Format(freed)) +
                            (unknown ? " " + T("Some items free space that can't be measured in advance.") : "") +
                            (failures > 0 ? " " + F("{0} items could not be cleaned; see Activity.", failures) : "");
        }
        finally
        {
            IsBusy = false;
            IsCleaning = false;
            _cleanCts.Dispose();
            _cleanCts = null;
        }

        await _afterClean();
        await AnalyzeAsync();
    }

    [RelayCommand]
    private void CancelClean()
    {
        _cleanCts?.Cancel();
        ProgressText = T("Cancelling...");
    }

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var item in _all)
            item.IsSelected = item.IsReady && item.Rule.IsRecommended;
    }

    [RelayCommand]
    private void SelectAllReady()
    {
        foreach (var item in _all)
            item.IsSelected = item.IsReady;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in _all)
            item.IsSelected = false;
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        foreach (var group in _all
                     .Where(i => ShowEmpty || !i.IsEmpty)
                     .GroupBy(i => i.Category)
                     .OrderByDescending(g => g.Sum(i => i.Bytes)))
        {
            Groups.Add(new FindingGroupViewModel(group.Key, group.OrderByDescending(i => i.Bytes).ToList()));
        }
    }

    private void UpdateSelection()
    {
        var selected = _all.Where(i => i.IsSelected).ToList();
        SelectedSizeText = ByteSize.Format(selected.Where(i => i.Finding.SizeKnown).Sum(i => i.Bytes));
        SelectedCountText = selected.Count == 0 ? T("Nothing selected") : selected.Count == 1 ? T("1 item selected") : F("{0} items selected", selected.Count);
        HasSelection = selected.Count > 0;
    }
}
