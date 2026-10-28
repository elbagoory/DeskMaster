#pragma warning disable CS0618 // Bitmap.Save(string) is fine for a dev tool
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskMaster.App.Services;
using DeskMaster.App.ViewModels;
using DeskMaster.App.Views;
using DeskMaster.Core.Settings;

// Usage: Shots <outDir> <scanPath> [lang]
var outDir = args.Length > 0 ? args[0] : ".";
var scanPath = args.Length > 1 ? args[1] : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var lang = args.Length > 2 ? args[2] : "en";
Directory.CreateDirectory(outDir);

using var session = HeadlessUnitTestSession.StartNew(typeof(ShotsEntry));
session.Dispatch(() =>
{
    DeskMaster.App.Localization.Loc.Initialize(lang);
    void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
    void Wait(Task t, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!t.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
            if (sw.Elapsed > TimeSpan.FromMinutes(4)) throw new TimeoutException(what);
        }
        t.GetAwaiter().GetResult();
        Console.WriteLine($"{what} done in {sw.Elapsed.TotalSeconds:0.0}s");
    }
    void SaveWindow(Window w, string name)
    {
        Pump();
        w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png"));
        Console.WriteLine("saved " + name);
    }

    var settings = new AppSettings { ProMode = false };
    var window = new MainWindow { Width = 1200, Height = 900 };
    var vm = new MainViewModel(new StubUi(), settings);
    window.DataContext = vm;
    window.Show();

    Wait(vm.InitializeAsync(), "init");
    SaveWindow(window, "1-simple-light");
    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
    SaveWindow(window, "2-simple-dark");
    Application.Current!.RequestedThemeVariant = ThemeVariant.Light;

    vm.IsProMode = true;
    Pump();
    var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
    Wait(vm.Explorer.ScanPathAsync(scanPath), "scan");
    var names = new[] { "explorer", "largest", "duplicates", "cleanup", "move", "activity" };
    for (var i = 0; i < names.Length; i++)
    {
        tabs.SelectedIndex = i;
        if (names[i] == "duplicates")
        {
            vm.Duplicates.ScanPath = scanPath;
            Pump();
            Wait(vm.Duplicates.ScanCommand.ExecuteAsync(null), "duplicates");
        }
        SaveWindow(window, $"3-pro-{i}-{names[i]}");
    }

    var settingsWindow = new SettingsWindow { DataContext = new SettingsViewModel(new AppSettings(), _ => { }), Height = 900 };
    settingsWindow.Show();
    SaveWindow(settingsWindow, "4-settings");
    settingsWindow.Close();

    var note = new NotificationWindow("Low disk space", "Windows-SSD (C:) is almost full: 8.20 GB free of 402 GB. Open DeskMaster to free some space.", "Free up space", () => { });
    note.Show();
    SaveWindow(note, "5-notification");
    note.Close();

    var dialog = new ConfirmWindow("Move to Recycle Bin?", "Move the folder Temp (10.1 GB, 17,319 files) to the Recycle Bin?\n\nYou can restore it from there.", "Move to Recycle Bin", true);
    dialog.Show();
    SaveWindow(dialog, "6-confirm");
    dialog.Close();
    vm.IsProMode = false;
}, CancellationToken.None).GetAwaiter().GetResult();

public static class ShotsEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DeskMaster.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

internal sealed class StubUi : IUiServices
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false) => Task.FromResult(false);
    public Task AlertAsync(string title, string message) => Task.CompletedTask;
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public void ShowSettings() { }
    public void Notify(string title, string message, string actionText, Action action) { }
    public void Shutdown() { }
}
