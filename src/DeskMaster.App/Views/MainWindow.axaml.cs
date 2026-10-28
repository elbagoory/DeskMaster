using Avalonia.Controls;

namespace DeskMaster.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FlowDirection = Localization.Loc.FlowDirection;
    }
}
