using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SDM.App.ViewModels;

namespace SDM.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>A maximised WindowChrome window overhangs the screen by the resize border: pad it back in.</summary>
    private void OnStateChanged(object? sender, EventArgs e) =>
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

    /// <summary>Ctrl+V anywhere except a text box pastes links (several links open the batch window).</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && e.OriginalSource is not TextBox)
        {
            Vm?.PasteLinks();
            e.Handled = true;
        }
    }

    private void OnCardDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm?.Selected is { } item)
        {
            if (item.IsCompleted) Vm.OpenFileCommand.Execute(item);
            else Vm.ItemActionCommand.Execute(item);
        }
    }

    /// <summary>The "⋯" button opens the card's context menu.</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        DependencyObject? node = button;
        while (node is not null && !(node is Border b && b.ContextMenu is not null))
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        if (node is Border card)
        {
            card.ContextMenu!.PlacementTarget = card;
            card.ContextMenu.Placement = PlacementMode.Bottom;
            card.ContextMenu.DataContext = card.DataContext;
            card.ContextMenu.IsOpen = true;
        }
    }
}
