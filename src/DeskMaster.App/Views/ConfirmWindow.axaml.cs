using Avalonia.Controls;

namespace DeskMaster.App.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
        FlowDirection = Localization.Loc.FlowDirection;
    }

    public ConfirmWindow(string title, string message, string confirmText, bool destructive, bool showCancel = true) : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.IsVisible = showCancel;
        if (destructive)
            ConfirmButton.Classes.Add("danger");
        ConfirmButton.Click += (_, _) =>
        {
            Result = true;
            Close(true);
        };
        CancelButton.Click += (_, _) => Close(false);
    }

    /// <summary>True when the confirm button was pressed (for non-modal use).</summary>
    public bool Result { get; private set; }
}
