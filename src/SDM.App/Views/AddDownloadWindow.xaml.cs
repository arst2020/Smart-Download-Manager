using System.Windows;
using SDM.App.Localization;
using SDM.App.ViewModels;

namespace SDM.App.Views;

public partial class AddDownloadWindow : Window
{
    public AddDownloadWindow() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UrlBox.Focus();
        UrlBox.SelectAll();
        if (DataContext is AddDownloadViewModel { IsValidUrl: true } vm)
            await vm.AnalyzeAsync(); // URL came from the clipboard or the command line
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (DataContext is AddDownloadViewModel { IsValidUrl: false })
        {
            MessageBox.Show(this, Loc.Get("InvalidUrl"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
