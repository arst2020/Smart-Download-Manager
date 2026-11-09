using System.Windows;

namespace SDM.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
