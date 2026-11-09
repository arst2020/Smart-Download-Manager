using System.Windows;
using System.Windows.Input;
using SDM.App.ViewModels;

namespace SDM.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.OpenFolderCommand.CanExecute(null))
            vm.OpenFolderCommand.Execute(null);
    }
}
