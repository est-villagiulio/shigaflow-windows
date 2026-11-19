using System.Windows;

namespace Shigaflow;

public partial class UrlDialog : Window
{
    public string Url => UrlBox.Text;
    public UrlDialog(string currentUrl)
    {
        InitializeComponent();
        UrlBox.Text = currentUrl;
        UrlBox.SelectAll();
        Loaded += (_, _) => UrlBox.Focus();
    }
    private void Use_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
