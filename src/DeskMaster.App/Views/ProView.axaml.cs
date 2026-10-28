using Avalonia.Controls;
using Avalonia.Input;
using DeskMaster.App.ViewModels;

namespace DeskMaster.App.Views;

public partial class ProView : UserControl
{
    public ProView()
    {
        InitializeComponent();
    }

    private void OnChildKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: NodeItemViewModel item })
            return;
        if (e.Key == Key.Delete)
        {
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? item.DeletePermanentlyCommand : item.RecycleCommand;
            command.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && item.OpenCommand.CanExecute(null))
        {
            item.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnChildDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: NodeItemViewModel item } && item.OpenCommand.CanExecute(null))
            item.OpenCommand.Execute(null);
    }
}
