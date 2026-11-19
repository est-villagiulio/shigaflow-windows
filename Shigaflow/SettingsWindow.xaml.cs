using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Windows.Navigation;

namespace Shigaflow;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    public AppSettings UpdatedSettings { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        UpdatedSettings = settings;
        ThemeBox.SelectedIndex = string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        StartupCheck.IsChecked = settings.StartWithWindows;
        SpeedLimitCheck.IsChecked = settings.SpeedLimitEnabled;
        AllowedExtensionsBox.Text = settings.AllowedExtensions;
        SpeedValueBox.Text = settings.SpeedLimitValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SpeedUnitBox.SelectedIndex = settings.SpeedLimitUnit == "KB/s" ? 0 : 1;
        UpdateSpeedOptions();
        TorrentDownloadLimitCheck.IsChecked = settings.TorrentDownloadLimitEnabled;
        TorrentDownloadValueBox.Text = settings.TorrentDownloadLimitValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TorrentDownloadUnitBox.SelectedIndex = settings.TorrentDownloadLimitUnit == "KB/s" ? 0 : 1;
        TorrentUploadLimitCheck.IsChecked = settings.TorrentUploadLimitEnabled;
        TorrentUploadValueBox.Text = settings.TorrentUploadLimitValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TorrentUploadUnitBox.SelectedIndex = settings.TorrentUploadLimitUnit == "KB/s" ? 0 : 1;
        TorrentAfterFinishBox.SelectedIndex = string.Equals(settings.TorrentAfterFinishAction, "Stop", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private void SpeedLimitCheck_Changed(object sender, RoutedEventArgs e) => UpdateSpeedOptions();

    private void UpdateSpeedOptions()
    {
        if (SpeedOptions != null) SpeedOptions.IsEnabled = SpeedLimitCheck.IsChecked == true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(SpeedValueBox.Text.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double speed) || speed <= 0 || double.IsInfinity(speed) || double.IsNaN(speed))
        {
            MessageBox.Show(this, "Enter a speed limit greater than zero.", "Invalid speed limit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (TorrentDownloadLimitCheck.IsChecked == true && !TryReadPositive(TorrentDownloadValueBox.Text, out _))
        {
            MessageBox.Show(this, "Enter a valid torrent download limit greater than zero, or turn the limit off.", "Invalid torrent download limit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (TorrentUploadLimitCheck.IsChecked == true && !TryReadPositive(TorrentUploadValueBox.Text, out _))
        {
            MessageBox.Show(this, "Enter a valid torrent upload limit greater than zero, or turn the limit off.", "Invalid torrent upload limit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool startWithWindows = StartupCheck.IsChecked == true;
        try
        {
            SetWindowsStartup(startWithWindows);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not update the Windows startup setting.\n\n" + ex.Message,
                "Startup setting failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var updated = new AppSettings
        {
            StartWithWindows = startWithWindows,
            Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Light" ? "Light" : "Dark",
            SpeedLimitEnabled = SpeedLimitCheck.IsChecked == true,
            SpeedLimitValue = speed,
            SpeedLimitUnit = (SpeedUnitBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "KB/s" ? "KB/s" : "MB/s",
            AllowedExtensions = AppSettings.NormalizeAllowedExtensions(AllowedExtensionsBox.Text),
            TorrentDownloadLimitEnabled = TorrentDownloadLimitCheck.IsChecked == true,
            TorrentDownloadLimitValue = TryReadPositive(TorrentDownloadValueBox.Text, out double torrentDown) ? torrentDown : 2,
            TorrentDownloadLimitUnit = (TorrentDownloadUnitBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "KB/s" ? "KB/s" : "MB/s",
            TorrentUploadLimitEnabled = TorrentUploadLimitCheck.IsChecked == true,
            TorrentUploadLimitValue = TryReadPositive(TorrentUploadValueBox.Text, out double torrentUp) ? torrentUp : 1,
            TorrentUploadLimitUnit = (TorrentUploadUnitBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "KB/s" ? "KB/s" : "MB/s",
            TorrentAfterFinishAction = (TorrentAfterFinishBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Stop" ? "Stop" : "Seed"
        };
        try { updated.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save settings.\n\n" + ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        UpdatedSettings = updated;
        App.ApplyTheme(updated.Theme);
        DialogResult = true;
    }

    private static bool TryReadPositive(string text, out double value)
    {
        return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value)
            && value > 0 && double.IsFinite(value);
    }

    private static void SetWindowsStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
            ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        const string valueName = "Shigaflow";
        if (!enabled) { key.DeleteValue(valueName, throwOnMissingValue: false); return; }

        string executable = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Could not determine the application executable path.");
        key.SetValue(valueName, "\"" + executable + "\"");
    }

    private void KLHWLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open the map link.\\n\\n" + ex.Message, "Open map", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        e.Handled = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
