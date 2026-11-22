using System.Windows;

namespace Shigaflow;

public partial class AddDownloadWindow : Window
{
    public string DownloadUrl { get; private set; } = string.Empty;

    public AddDownloadWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UrlInput.Focus();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        DownloadUrl = UrlInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(DownloadUrl))
        {
            MessageBox.Show(this, "Paste a download URL first.", "Add download", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
