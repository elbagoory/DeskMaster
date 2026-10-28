using Avalonia.Controls;

namespace DeskMaster.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        FlowDirection = Localization.Loc.FlowDirection;
    }
}
