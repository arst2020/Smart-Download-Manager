using System.Windows;
using SDM.App.Localization;
using SDM.App.ViewModels;

namespace SDM.App.Views;

public partial class AddDownloadWindow : Window
{
    public AddDownloadWindow() => InitializeComponent();

    private AddDownloadViewModel? Vm => DataContext as AddDownloadViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UrlBox.Focus();
        UrlBox.SelectAll();
        if (Vm is { IsValidUrl: true } vm)
            await vm.AnalyzeAsync(); // URL came from the clipboard or the command line
    }

    private void OnStart(object sender, RoutedEventArgs e) => Finish(scheduled: false);

    private void OnSchedule(object sender, RoutedEventArgs e) => Finish(scheduled: true);

    private void Finish(bool scheduled)
    {
        if (Vm is not { } vm) return;
        if (!vm.IsValidUrl)
        {
            MessageBox.Show(this, Loc.Get("InvalidUrl"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        vm.Scheduled = scheduled;
        DialogResult = true;
    }
}
