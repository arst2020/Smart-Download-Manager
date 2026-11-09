using System.Windows;

namespace SDM.App.Views;

public partial class SchedulerWindow : Window
{
    public SchedulerWindow() => InitializeComponent();

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
