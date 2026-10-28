using static DeskMaster.App.Localization.Loc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMaster.Core.Platform;
using DeskMaster.Core.Settings;

namespace DeskMaster.App.ViewModels;

public sealed record Choice(string Value, string Label);

/// <summary>Every option in the Settings window; each change is saved immediately.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _applied;
    private readonly Func<Task<string>>? _checkForUpdates;
    private bool _loading = true;

    public SettingsViewModel(AppSettings settings, Action<AppSettings> applied,
        Func<Task<string>>? checkForUpdates = null, string? version = null)
    {
        _settings = settings;
        _applied = applied;
        _checkForUpdates = checkForUpdates;
        VersionText = "DeskMaster " + (version ?? "0.1.0");
        Theme = Themes.FirstOrDefault(t => t.Value == settings.Theme) ?? Themes[0];
        Language = Languages.FirstOrDefault(l => l.Value == settings.Language) ?? Languages[0];
        KeepRunningInTray = settings.KeepRunningInTray;
        StartWithSystem = Autostart.IsEnabled();
        LowSpaceAlerts = settings.LowSpaceAlerts;
        LowSpacePercent = settings.LowSpacePercent;
        LowSpaceGb = settings.LowSpaceGb;
        AutoClean = settings.AutoClean;
        AutoCleanDays = settings.AutoCleanDays;
        CheckForUpdates = settings.CheckForUpdates;
        _loading = false;
    }

    public IReadOnlyList<Choice> Themes { get; } = [new("system", T("Same as system")), new("light", T("Light")), new("dark", T("Dark"))];
    public IReadOnlyList<Choice> Languages { get; } = [new("", T("Same as system")), new("en", "English"), new("ar", "العربية")];
    public IReadOnlyList<int> DayOptions { get; } = [1, 3, 7, 14, 30];

    public string StartWithSystemLabel => PlatformInfo.Os switch
    {
        OsKind.Windows => T("Start with Windows (in the tray)"),
        OsKind.MacOS => T("Open at login (in the menu bar)"),
        _ => T("Start when I log in (in the tray)"),
    };

    public string LastAutoCleanText => _settings.LastAutoCleanUtc is { } t
        ? T("Last automatic cleanup:") + " " + t.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : T("Automatic cleanup has not run yet.");

    public string SettingsFolder => AppSettings.Folder;

    public string VersionText { get; }

    [ObservableProperty] public partial Choice Theme { get; set; }
    [ObservableProperty] public partial Choice Language { get; set; }
    [ObservableProperty] public partial bool KeepRunningInTray { get; set; }
    [ObservableProperty] public partial bool StartWithSystem { get; set; }
    [ObservableProperty] public partial bool LowSpaceAlerts { get; set; }
    [ObservableProperty] public partial int LowSpacePercent { get; set; }
    [ObservableProperty] public partial int LowSpaceGb { get; set; }
    [ObservableProperty] public partial bool AutoClean { get; set; }
    [ObservableProperty] public partial int AutoCleanDays { get; set; }
    [ObservableProperty] public partial bool CheckForUpdates { get; set; }
    [ObservableProperty] public partial string? Notice { get; set; }

    partial void OnStartWithSystemChanged(bool value)
    {
        if (_loading)
            return;
        if (!Autostart.Set(value))
            Notice = T("Could not change the startup setting.");
        else if (value && !KeepRunningInTray)
            KeepRunningInTray = true; // Starting in the background needs the tray.
    }

    partial void OnLanguageChanged(Choice value)
    {
        if (!_loading)
            Notice = T("The new language is used everywhere after you restart DeskMaster.");
        Save();
    }

    partial void OnThemeChanged(Choice value) => Save();
    partial void OnKeepRunningInTrayChanged(bool value) => Save();
    partial void OnLowSpaceAlertsChanged(bool value) => Save();
    partial void OnLowSpacePercentChanged(int value) => Save();
    partial void OnLowSpaceGbChanged(int value) => Save();
    partial void OnAutoCleanChanged(bool value) => Save();
    partial void OnAutoCleanDaysChanged(int value) => Save();
    partial void OnCheckForUpdatesChanged(bool value) => Save();

    [ObservableProperty] public partial string? UpdateStatus { get; set; }

    [RelayCommand]
    private async Task CheckUpdatesNowAsync()
    {
        if (_checkForUpdates is null)
            return;
        UpdateStatus = T("Checking...");
        UpdateStatus = await _checkForUpdates();
    }

    [RelayCommand]
    private void OpenSettingsFolder() => PlatformInfo.OpenInFileManager(AppSettings.Folder);

    private void Save()
    {
        if (_loading)
            return;
        _settings.Theme = Theme.Value;
        _settings.Language = Language.Value;
        _settings.KeepRunningInTray = KeepRunningInTray;
        _settings.StartWithSystem = StartWithSystem;
        _settings.LowSpaceAlerts = LowSpaceAlerts;
        _settings.LowSpacePercent = Math.Clamp(LowSpacePercent, 1, 50);
        _settings.LowSpaceGb = Math.Clamp(LowSpaceGb, 1, 500);
        _settings.AutoClean = AutoClean;
        _settings.AutoCleanDays = AutoCleanDays;
        _settings.CheckForUpdates = CheckForUpdates;
        _settings.Save();
        _applied(_settings);
    }
}
