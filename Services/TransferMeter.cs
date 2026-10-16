using System.Diagnostics;
namespace DriveLink.Services;

internal sealed class TransferMeter(IProgress<TransferProgress>? progress, long? total)
{
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private long previousBytes;
    private double previousTime;
    public void Report(long bytes, bool force = false)
    {
        var now = watch.Elapsed.TotalSeconds;
        var elapsed = now - previousTime;
        if (!force && elapsed < 0.2) return;
        var speed = elapsed > 0 ? Math.Max(0, bytes - previousBytes) / elapsed : 0;
        progress?.Report(new(bytes, total, speed));
        previousBytes = bytes; previousTime = now;
    }
}
