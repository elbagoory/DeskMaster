using static DeskMaster.App.Localization.Loc;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMaster.Core.Settings;

namespace DeskMaster.App.ViewModels;

public sealed record LogEntry(DateTime Time, string Message, bool IsError)
{
    public string TimeText => Time.Date == DateTime.Today ? Time.ToString("HH:mm:ss") : Time.ToString("MM-dd HH:mm");
}

/// <summary>Everything DeskMaster did, newest first. Saved to monthly log files.</summary>
public sealed partial class ActivityLogViewModel : ViewModelBase
{
    private readonly ActivityLogStore? _store;

    public ActivityLogViewModel(ActivityLogStore? store = null)
    {
        _store = store;
        if (_store is null)
            return;
        foreach (var e in _store.LoadRecent())
            Entries.Add(new LogEntry(e.Time, e.Message, e.IsError));
    }

    public ObservableCollection<LogEntry> Entries { get; } = [];

    [ObservableProperty]
    public partial string LatestMessage { get; set; } = T("Ready");

    public void Info(string message) => Add(message, false);

    public void Error(string message) => Add(message, true);

    private void Add(string message, bool isError)
    {
        var entry = new LogEntry(DateTime.Now, message, isError);
        _store?.Append(new StoredLogEntry(entry.Time, message, isError));
        void Insert()
        {
            Entries.Insert(0, entry);
            if (Entries.Count > 1000)
                Entries.RemoveAt(Entries.Count - 1);
            LatestMessage = message;
        }

        if (Dispatcher.UIThread.CheckAccess())
            Insert();
        else
            Dispatcher.UIThread.Post(Insert);
    }

    [RelayCommand]
    private void Clear() => Entries.Clear();
}
