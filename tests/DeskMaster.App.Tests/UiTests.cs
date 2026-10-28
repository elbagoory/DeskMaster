using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using DeskMaster.App.Localization;
using DeskMaster.App.ViewModels;
using DeskMaster.App.Views;
using DeskMaster.Core.Settings;

namespace DeskMaster.App.Tests;

public class UiTests
{
    private static (MainWindow Window, MainViewModel Vm, FakeUi Ui) Open(bool pro = false)
    {
        Loc.Initialize("en");
        var ui = new FakeUi();
        var vm = new MainViewModel(ui, new AppSettings { ProMode = pro });
        var window = new MainWindow { DataContext = vm };
        window.Show();
        return (window, vm, ui);
    }

    [AvaloniaFact]
    public void Simple_mode_shows_drives_and_switching_to_pro_shows_all_tabs()
    {
        var (window, vm, _) = Open();
        Assert.NotEmpty(vm.Drives);
        Assert.True(window.GetVisualDescendants().OfType<SimpleView>().Single().IsVisible);

        vm.IsProMode = true;
        window.UpdateLayout(); // the Pro view builds its tabs on its first layout
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        Assert.True(window.GetVisualDescendants().OfType<ProView>().Single().IsVisible);
        Assert.Equal(["Explorer", "Largest files", "Duplicates", "Cleanup", "Move to another drive", "Activity"],
            tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString()));
        vm.IsProMode = false;
    }

    [AvaloniaFact]
    public void Arabic_turns_the_window_right_to_left()
    {
        try
        {
            Loc.Initialize("ar");
            var window = new MainWindow { DataContext = new MainViewModel(new FakeUi(), new AppSettings()) };
            window.Show();
            Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "إخلاء مساحة");
        }
        finally
        {
            Loc.Initialize("en");
        }
    }

    [AvaloniaFact]
    public async Task Analysis_fills_the_cleanup_lists()
    {
        var (_, vm, _) = Open();
        await vm.Cleanup.AnalyzeAsync();
        Assert.True(vm.Cleanup.HasResults);
        Assert.NotEmpty(vm.Cleanup.Groups);
        Assert.False(vm.Cleanup.IsBusy);
    }

    [AvaloniaFact]
    public async Task Explorer_scans_navigates_and_deletes_with_confirmation()
    {
        using var dir = new TempDir();
        dir.File("big/movie.mp4", 50_000);
        dir.File("small.txt", 100);
        var (_, vm, ui) = Open(pro: true);

        await vm.Explorer.ScanPathAsync(dir.Path);
        Assert.True(vm.Explorer.HasScan);
        Assert.Equal("big", vm.Explorer.Children[0].Name);

        vm.Explorer.Children[0].OpenCommand.Execute(null);
        Assert.Equal("big", vm.Explorer.Current!.Name);
        Assert.True(vm.Explorer.CanGoUp);

        ui.Answer = false;
        await vm.Explorer.DeleteAsync(vm.Explorer.Current!, permanently: true);
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "big")));

        ui.Answer = true;
        await vm.Explorer.DeleteAsync(vm.Explorer.Current!, permanently: true);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "big")));
        Assert.Equal(100, vm.Explorer.Current!.Size);
        Assert.Equal(2, ui.Confirmations.Count);
    }

    [AvaloniaFact]
    public async Task Duplicates_keep_one_copy_of_every_file()
    {
        using var dir = new TempDir();
        var a = dir.File("a/song.mp3", 2 * 1024 * 1024);
        File.Copy(a, Path.Combine(dir.Path, "song (copy).mp3"));
        var (_, vm, ui) = Open(pro: true);
        vm.Duplicates.ScanPath = dir.Path;

        await vm.Duplicates.ScanCommand.ExecuteAsync(null);
        var group = Assert.Single(vm.Duplicates.Groups);

        foreach (var f in group.Files)
            f.IsSelected = true;
        await vm.Duplicates.RecycleSelectedCommand.ExecuteAsync(null);
        Assert.Single(ui.Alerts); // refused: every copy was selected
        Assert.All(group.Files, f => Assert.True(File.Exists(f.Path)));

        vm.Duplicates.SelectAllButNewestCommand.Execute(null);
        Assert.Equal(1, group.Files.Count(f => f.IsSelected));
        Assert.True(vm.Duplicates.HasSelection);
    }

    [AvaloniaFact]
    public void Settings_window_saves_changes()
    {
        Loc.Initialize("en");
        var settings = new AppSettings();
        AppSettings? applied = null;
        var vm = new SettingsViewModel(settings, s => applied = s);
        var window = new SettingsWindow { DataContext = vm };
        window.Show();

        vm.Theme = vm.Themes.Single(t => t.Value == "dark");
        vm.LowSpaceGb = 30;

        Assert.Same(settings, applied);
        Assert.Equal("dark", settings.Theme);
        Assert.Equal(30, settings.LowSpaceGb);
    }

    [AvaloniaFact]
    public void Confirm_dialog_returns_the_choice()
    {
        Loc.Initialize("en");
        var dialog = new ConfirmWindow("Delete?", "Really?", "Delete", destructive: true);
        dialog.Show();
        var confirm = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Delete");
        Assert.Contains("danger", confirm.Classes);
        confirm.Command?.Execute(null);
        confirm.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(dialog.Result);
    }
}
