using static DeskMaster.App.Localization.Loc;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using DeskMaster.App.Services;
using DeskMaster.App.ViewModels;
using DeskMaster.App.Views;
using DeskMaster.Core.Cleanup;
using DeskMaster.Core.Drives;
using DeskMaster.Core.Formatting;
using DeskMaster.Core.Monitoring;
using DeskMaster.Core.Platform;
using DeskMaster.Core.Settings;

namespace DeskMaster.App;

public partial class App : Application
{
    private readonly LowSpaceMonitor _monitor = new();
    private readonly UpdateService _updates = new();
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private AppSettings _settings = new();
    private DispatcherTimer? _timer;
    private bool _quitting;
    private bool _toldAboutTray;
    private bool _initialized;
    private bool _autoCleanRunning;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _settings = AppSettings.Load();
            ApplySettings(_settings);
            Localization.Loc.Initialize(_settings.Language);

            _window = new MainWindow();
            var services = new UiServices(_window, Quit, () => new SettingsWindow
            {
                DataContext = new SettingsViewModel(_settings, ApplySettings, () => CheckForUpdatesAsync(manual: true), _updates.CurrentVersion),
            });
            _vm = new MainViewModel(services, _settings);
            _window.DataContext = _vm;
            _window.Opened += async (_, _) => await InitializeOnceAsync();
            _window.Closing += OnWindowClosing;

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SetUpTray();
            StartBackgroundChecks();

            var background = desktop.Args?.Contains(Autostart.BackgroundArgument) == true && _settings.KeepRunningInTray;
            if (!background)
                ShowMainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void ApplySettings(AppSettings settings)
    {
        RequestedThemeVariant = settings.Theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private async Task InitializeOnceAsync()
    {
        if (_initialized || _vm is null)
            return;
        _initialized = true;
        await _vm.InitializeAsync();
        if (_settings.CheckForUpdates)
            await CheckForUpdatesAsync(manual: false);
    }

    private void ShowMainWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting || !_settings.KeepRunningInTray)
        {
            Quit();
            return;
        }

        e.Cancel = true;
        _window?.Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            _vm?.Log.Info(T("DeskMaster keeps running in the tray to watch your free space. Right-click the tray icon to quit."));
        }
    }

    private void Quit()
    {
        _quitting = true;
        _timer?.Stop();
        _desktop?.Shutdown();
    }

    private void SetUpTray()
    {
        try
        {
            var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://DeskMaster/Assets/deskmaster.ico")));
            var menu = new NativeMenu();
            menu.Add(MenuItem(T("Open DeskMaster"), ShowMainWindow));
            menu.Add(MenuItem(T("Clean safe items now"), () => _ = RunAutoCleanAsync(manual: true)));
            menu.Add(MenuItem(T("Settings"), () => _vm?.OpenSettingsCommand.Execute(null)));
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(MenuItem(T("Quit DeskMaster"), Quit));

            var tray = new TrayIcon { Icon = icon, ToolTipText = "DeskMaster", Menu = menu, IsVisible = true };
            tray.Clicked += (_, _) => ShowMainWindow();
            TrayIcon.SetIcons(this, [tray]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            // No tray on this desktop: closing the window quits instead.
            _settings.KeepRunningInTray = false;
        }
    }

    private static NativeMenuItem MenuItem(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private void StartBackgroundChecks()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromMinutes(15); // first check after a minute, then every 15
            CheckFreeSpace();
            if (AutoCleaner.IsDue(_settings, DateTime.UtcNow))
                await RunAutoCleanAsync(manual: false);
        };
        _timer.Start();
    }

    private void CheckFreeSpace()
    {
        if (_vm is null)
            return;
        foreach (var alert in _monitor.Check(DriveService.GetDrives(), _settings, DateTime.UtcNow))
        {
            _vm.Log.Info(alert.Message);
            _vm.Services.Notify(T("Low disk space"), alert.Message + " " + T("Open DeskMaster to free some space."), T("Free up space"), () =>
            {
                ShowMainWindow();
                _vm.IsProMode = false;
                _ = _vm.Cleanup.AnalyzeAsync();
            });
        }
    }

    /// <summary>Returns a short status for the Settings window.</summary>
    private async Task<string> CheckForUpdatesAsync(bool manual)
    {
        if (!_updates.CanUpdate)
            return T("Updates work in the installed version. This copy was not installed with the DeskMaster installer.");

        var update = await _updates.CheckAsync();
        if (update is null)
            return T("DeskMaster is up to date.");

        var version = update.TargetFullRelease.Version.ToString();
        _vm?.Log.Info(F("DeskMaster {0} is available.", version));
        _vm?.Services.Notify(T("Update available"), F("DeskMaster {0} is available. It installs in a few seconds and DeskMaster restarts.", version),
            T("Update and restart"), () => _ = InstallUpdateAsync(update));
        return F("DeskMaster {0} is available.", version);
    }

    private async Task InstallUpdateAsync(Velopack.UpdateInfo update)
    {
        try
        {
            _vm?.Log.Info(T("Downloading the update..."));
            _quitting = true;
            await _updates.DownloadAndRestartAsync(update);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            _quitting = false;
            _vm?.Log.Error(T("The update failed:") + " " + ex.Message);
        }
    }

    private async Task RunAutoCleanAsync(bool manual)
    {
        if (_vm is null || _autoCleanRunning)
            return;
        _autoCleanRunning = true;
        try
        {
            var result = await AutoCleaner.RunAsync(RuleCatalog.LoadForCurrentOs());
            _settings.LastAutoCleanUtc = DateTime.UtcNow;
            _settings.Save();
            var message = F(manual ? "Cleanup freed {0} from {1} items." : "Automatic cleanup freed {0} from {1} items.", ByteSize.Format(result.FreedBytes), result.Cleaned);
            _vm.Log.Info(message);
            _vm.RefreshDrivesCommand.Execute(null);
            if (manual || result.FreedBytes >= 100L * 1024 * 1024)
                _vm.Services.Notify("DeskMaster", message, T("Open"), ShowMainWindow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _vm.Log.Error(T("Automatic cleanup failed:") + " " + ex.Message);
        }
        finally
        {
            _autoCleanRunning = false;
        }
    }
}
