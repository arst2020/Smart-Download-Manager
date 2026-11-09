using System.Windows;
using SDM.App.ViewModels;

namespace SDM.App.Views;

public partial class BatchAddWindow : Window
{
    public BatchAddWindow() => InitializeComponent();

    private BatchAddViewModel? Vm => DataContext as BatchAddViewModel;

    private void OnStart(object sender, RoutedEventArgs e) => Finish(scheduled: false, paused: false);

    private void OnQueue(object sender, RoutedEventArgs e) => Finish(scheduled: false, paused: true);

    private void OnSchedule(object sender, RoutedEventArgs e) => Finish(scheduled: true, paused: false);

    private void Finish(bool scheduled, bool paused)
    {
        if (Vm is not { } vm) return;
        vm.Scheduled = scheduled;
        vm.AddPaused = paused;
        DialogResult = true;
    }
}
