namespace Shigaflow;

// Short-lived browser session cookie data passed only over the local native-messaging pipe.
// These values are never written to download history or application settings.
public sealed class BrowserCookie
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Path { get; set; } = "/";
    public bool Secure { get; set; }
    public bool HttpOnly { get; set; }
    public double? ExpirationDate { get; set; }
}
