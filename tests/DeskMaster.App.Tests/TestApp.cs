using Avalonia;
using Avalonia.Headless;
using DeskMaster.App.Services;

[assembly: AvaloniaTestApplication(typeof(DeskMaster.App.Tests.TestApp))]

namespace DeskMaster.App.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Records dialogs instead of showing them; answers confirmations with <see cref="Answer"/>.</summary>
internal sealed class FakeUi : IUiServices
{
    public bool Answer { get; set; } = true;
    public List<string> Confirmations { get; } = [];
    public List<string> Alerts { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
    {
        Confirmations.Add(title);
        return Task.FromResult(Answer);
    }

    public Task AlertAsync(string title, string message)
    {
        Alerts.Add(title);
        return Task.CompletedTask;
    }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public void ShowSettings() { }
    public void Notify(string title, string message, string actionText, Action action) { }
    public void Shutdown() { }
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deskmaster-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative, int bytes)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, new byte[bytes]);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}
