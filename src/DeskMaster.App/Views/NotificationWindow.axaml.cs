using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DeskMaster.App.Views;

/// <summary>A small pop-up in the screen corner, used while DeskMaster runs in the tray.</summary>
public partial class NotificationWindow : Window
{
    private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromSeconds(20) };

    public NotificationWindow()
    {
        InitializeComponent();
        FlowDirection = Localization.Loc.FlowDirection;
    }

    public NotificationWindow(string title, string message, string actionText, Action action) : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        ActionButton.Content = actionText;
        ActionButton.Click += (_, _) =>
        {
            Close();
            action();
        };
        DismissButton.Click += (_, _) => Close();
        _autoClose.Tick += (_, _) => Close();
        Opened += (_, _) =>
        {
            PlaceInCorner();
            _autoClose.Start();
        };
        PointerEntered += (_, _) => _autoClose.Stop();
        Closed += (_, _) => _autoClose.Stop();
    }

    private void PlaceInCorner()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)(Bounds.Width * scale);
        var height = (int)(Bounds.Height * scale);
        var margin = (int)(16 * scale);
        Position = new PixelPoint(area.Right - width - margin, area.Bottom - height - margin);
    }
}
