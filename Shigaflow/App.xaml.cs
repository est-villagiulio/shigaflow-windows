using System.Windows;
using System.Windows.Media;

namespace Shigaflow;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _showExistingEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool createdNew;
        _singleInstanceMutex = new Mutex(true, @"Local\Shigaflow.SingleInstance", out createdNew);
        _showExistingEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Shigaflow.ShowExisting");
        if (!createdNew)
        {
            // A previous instance may be running in the system tray. Ask it to show
            // its window instead of silently exiting this launch.
            try { _showExistingEvent.Set(); } catch { }
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        // Listen for later launches and restore this instance from the tray.
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    if (_showExistingEvent.WaitOne())
                        await Dispatcher.InvokeAsync(mainWindow.ShowFromExternalLaunch);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Single-instance activation: " + ex.Message);
                    await Task.Delay(250);
                }
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        _showExistingEvent?.Dispose();
        base.OnExit(e);
    }

    public static void ApplyTheme(string themeName)
    {
        if (Current == null) return;
        bool light = string.Equals(themeName, "Light", StringComparison.OrdinalIgnoreCase);
        SetColor("Bg", light ? "#F3F5F8" : "#101318");
        SetColor("Panel", light ? "#FFFFFF" : "#181D25");
        SetColor("PanelAlt", light ? "#E8EDF3" : "#202733");
        SetColor("Stroke", light ? "#D3DAE4" : "#303A49");
        SetColor("TextMain", light ? "#1C2532" : "#F0F4FA");
        SetColor("TextMuted", light ? "#596779" : "#9AA7B8");
        SetColor("Accent", light ? "#087F72" : "#68D5C4");
        SetColor("Blue", light ? "#245DB4" : "#7AA7FF");
        SetColor("Warn", light ? "#946200" : "#F2C879");
        SetColor("Red", light ? "#B42332" : "#FF7B86");
        SetColor("Hover", light ? "#E2E8F0" : "#202A36");
        SetColor("Selected", light ? "#D5E1EF" : "#26313E");
        SetColor("Header", light ? "#E9EEF4" : "#1D232D");
        SetColor("Track", light ? "#D7DEE8" : "#303846");
        SetColor("AccentText", light ? "#FFFFFF" : "#101318");
        SetBrush("BgBrush", "Bg");
        SetBrush("PanelBrush", "Panel");
        SetBrush("PanelAltBrush", "PanelAlt");
        SetBrush("StrokeBrush", "Stroke");
        SetBrush("TextBrush", "TextMain");
        SetBrush("MutedBrush", "TextMuted");
        SetBrush("AccentBrush", "Accent");
        SetBrush("BlueBrush", "Blue");
        SetBrush("WarnBrush", "Warn");
        SetBrush("RedBrush", "Red");
        SetBrush("HoverBrush", "Hover");
        SetBrush("SelectedBrush", "Selected");
        SetBrush("HeaderBrush", "Header");
        SetBrush("TrackBrush", "Track");
        SetBrush("AccentTextBrush", "AccentText");
        Current.Resources["WindowBackgroundBrush"] = Current.Resources["BgBrush"];
    }

    private static void SetColor(string key, string hex)
    {
        Current.Resources[key] = (Color)ColorConverter.ConvertFromString(hex);
    }

    private static void SetBrush(string brushKey, string colorKey)
    {
        var color = (Color)Current.Resources[colorKey];
        Current.Resources[brushKey] = new SolidColorBrush(color);
    }
}
