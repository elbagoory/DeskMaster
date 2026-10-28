using static DeskMaster.App.Localization.Loc;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMaster.App.Services;
using DeskMaster.Core.Drives;
using DeskMaster.Core.Platform;
using DeskMaster.Core.Settings;

namespace DeskMaster.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IUiServices _ui;
    private readonly AppSettings _settings;

    public MainViewModel(IUiServices ui, AppSettings settings)
    {
        _ui = ui;
        _settings = settings;
        Log = new ActivityLogViewModel(ActivityLogStore.Default);
        Cleanup = new CleanupViewModel(ui, Log, RefreshAfterChangeAsync);
        Explorer = new ExplorerViewModel(ui, Log);
        Duplicates = new DuplicatesViewModel(ui, Log);
        Move = new MoveViewModel(ui, Log, RefreshAfterChangeAsync);
        IsProMode = settings.ProMode;
        RefreshDrives();
    }

    public ActivityLogViewModel Log { get; }
    public IUiServices Services => _ui;
    public CleanupViewModel Cleanup { get; }
    public ExplorerViewModel Explorer { get; }
    public DuplicatesViewModel Duplicates { get; }
    public MoveViewModel Move { get; }
    public ObservableCollection<DriveItemViewModel> Drives { get; } = [];

    public string PlatformText => PlatformInfo.OsDescription;
    public bool IsElevated => PlatformInfo.IsElevated;
    public bool CanElevate => !PlatformInfo.IsElevated && PlatformInfo.CanRequestElevation;
    public bool ShowElevationHint => !PlatformInfo.IsElevated && !PlatformInfo.CanRequestElevation;
    public string ElevationHint => PlatformInfo.ElevationHint;
    public string VersionText => "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

    [ObservableProperty]
    public partial bool IsProMode { get; set; }

    public bool IsSimpleMode
    {
        get => !IsProMode;
        set => IsProMode = !value;
    }

    partial void OnIsProModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSimpleMode));
        _settings.ProMode = value;
        _settings.Save();
    }

    public async Task InitializeAsync()
    {
        Log.Info(F(IsElevated ? "DeskMaster started on {0} as administrator." : "DeskMaster started on {0}.", PlatformInfo.OsDescription));
        await Task.WhenAll(Cleanup.AnalyzeAsync(), Move.DetectAsync());
    }

    [RelayCommand]
    private void RefreshDrives()
    {
        Drives.Clear();
        foreach (var d in DriveService.GetDrives())
            Drives.Add(new DriveItemViewModel(d));
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        RefreshDrives();
        Explorer.RefreshTargets();
        await Task.WhenAll(Cleanup.AnalyzeAsync(), Move.DetectAsync());
    }

    [RelayCommand]
    private void OpenSettings() => _ui.ShowSettings();

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (PlatformInfo.TryRestartElevated())
            _ui.Shutdown();
        else
            Log.Error(T("Administrator restart was cancelled."));
    }

    private Task RefreshAfterChangeAsync()
    {
        RefreshDrives();
        return Task.CompletedTask;
    }
}
