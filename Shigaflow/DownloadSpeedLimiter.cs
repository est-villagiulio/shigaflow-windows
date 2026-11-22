using System.Diagnostics;

namespace Shigaflow;

/// <summary>A shared pacing limiter so the configured cap applies to all active downloads combined.</summary>
public sealed class DownloadSpeedLimiter
{
    private readonly object _gate = new();
    private double _bytesPerSecond;
    private long _nextSlotTimestamp;

    public void SetLimit(double bytesPerSecond)
    {
        lock (_gate)
        {
            _bytesPerSecond = Math.Max(0, bytesPerSecond);
            _nextSlotTimestamp = Stopwatch.GetTimestamp();
        }
    }

    public async Task WaitForTurnAsync(int bytes, CancellationToken cancellationToken)
    {
        long slot;
        double rate;
        lock (_gate)
        {
            rate = _bytesPerSecond;
            if (rate <= 0 || bytes <= 0) return;
            long now = Stopwatch.GetTimestamp();
            slot = Math.Max(now, _nextSlotTimestamp);
            long duration = (long)Math.Ceiling(bytes / rate * Stopwatch.Frequency);
            _nextSlotTimestamp = slot + Math.Max(1, duration);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remaining = slot - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            var delay = TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency);
            if (delay > TimeSpan.FromMilliseconds(250)) delay = TimeSpan.FromMilliseconds(250);
            await Task.Delay(delay, cancellationToken);
        }
    }
}
