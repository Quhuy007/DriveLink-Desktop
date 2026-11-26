namespace DriveLink.Services;

// Each lane starts jobs in queue order. By default one job runs at a time; with a limit function
// (adaptive parallelism) several jobs of the same direction run together. Separate lanes never hold each other's work.
public sealed class TransferLane(Func<int>? limit = null)
{
    private sealed record Job(Func<CancellationToken, Task> Work, TaskCompletionSource Completion, CancellationTokenSource Source);
    private readonly object gate = new();
    private readonly Queue<Job> pending = new();
    private readonly HashSet<Job> active = [];
    public event Action? Changed;
    public int Count { get { lock (gate) return pending.Count + active.Count; } }
    public int Running { get { lock (gate) return active.Count; } }
    public int Waiting { get { lock (gate) return pending.Count; } }
    private int Limit => Math.Max(1, limit?.Invoke() ?? 1);

    public Task Enqueue(Func<CancellationToken, Task> work) => Enqueue(work, out _);

    /// <summary>Thêm việc vào hàng; trả về token hủy riêng của việc đó qua <paramref name="cancel"/>.</summary>
    public Task Enqueue(Func<CancellationToken, Task> work, out Action cancel)
    {
        var job = new Job(work, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new CancellationTokenSource());
        cancel = () => Cancel(job);
        lock (gate) pending.Enqueue(job);
        Changed?.Invoke();
        Pump();
        return job.Completion.Task;
    }

    /// <summary>Khởi động thêm việc khi còn chỗ (gọi lại khi giới hạn song song tăng).</summary>
    public void Pump()
    {
        List<Job> starting = [];
        lock (gate)
        {
            while (pending.Count > 0 && active.Count < Limit)
            {
                var job = pending.Dequeue();
                if (job.Source.IsCancellationRequested) { job.Completion.TrySetCanceled(); job.Source.Dispose(); continue; }
                active.Add(job); starting.Add(job);
            }
        }
        foreach (var job in starting) _ = RunAsync(job);
        if (starting.Count > 0) Changed?.Invoke();
    }

    private async Task RunAsync(Job job)
    {
        try { await Task.Yield(); await job.Work(job.Source.Token); job.Completion.TrySetResult(); }
        catch (OperationCanceledException) { job.Completion.TrySetCanceled(); }
        catch (Exception ex) { job.Completion.TrySetException(ex); }
        finally
        {
            lock (gate) active.Remove(job);
            job.Source.Dispose();
            Changed?.Invoke();
            Pump();
        }
    }

    private void Cancel(Job job)
    {
        lock (gate)
        {
            if (active.Contains(job)) { try { job.Source.Cancel(); } catch (ObjectDisposedException) { } return; }
            if (!pending.Contains(job)) return;
            var rest = pending.Where(x => x != job).ToList();
            pending.Clear(); foreach (var item in rest) pending.Enqueue(item);
            job.Completion.TrySetCanceled(); job.Source.Dispose();
        }
        Changed?.Invoke();
    }

    public void CancelAll()
    {
        lock (gate)
        {
            foreach (var job in active) { try { job.Source.Cancel(); } catch (ObjectDisposedException) { } }
            while (pending.TryDequeue(out var job)) { job.Completion.TrySetCanceled(); job.Source.Dispose(); }
        }
        Changed?.Invoke();
    }
}

/// <summary>
/// Tự điều chỉnh số tệp truyền song song trong khoảng 2–6: tăng khi thêm luồng làm tổng tốc độ tăng rõ,
/// giảm khi mạng phải thử lại (chập chờn) hoặc tốc độ tụt. Bắt đầu với 3.
/// </summary>
public sealed class AdaptiveConcurrency(int minimum = 2, int maximum = 6, int initial = 3)
{
    private readonly object gate = new();
    private double previous;
    private DateTimeOffset calmUntil = DateTimeOffset.MinValue;
    public int Minimum { get; } = minimum;
    public int Maximum { get; } = maximum;
    public int Current { get; private set; } = Math.Clamp(initial, minimum, maximum);
    public event Action? Changed;

    /// <summary>Có lỗi tạm thời: bớt một luồng và giữ nguyên ít nhất 30 giây.</summary>
    public void ReportRetry(DateTimeOffset now)
    {
        lock (gate)
        {
            calmUntil = now + TimeSpan.FromSeconds(30);
            if (Current <= Minimum) return;
            Current--; previous = 0;
        }
        Changed?.Invoke();
    }

    /// <summary>Gọi định kỳ (mỗi ~10 giây) với tổng tốc độ của hướng đó và việc còn chờ.</summary>
    public void Sample(double bytesPerSecond, bool hasWaitingWork, DateTimeOffset now)
    {
        var changed = false;
        lock (gate)
        {
            if (now < calmUntil || !hasWaitingWork) { previous = bytesPerSecond; return; }
            if (previous <= 0 || bytesPerSecond >= previous * 1.1) { if (Current < Maximum) { Current++; changed = true; } }
            else if (bytesPerSecond < previous * 0.8 && Current > Minimum) { Current--; changed = true; }
            previous = bytesPerSecond;
        }
        if (changed) Changed?.Invoke();
    }
}
