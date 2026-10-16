namespace DriveLink.Services;

// Each lane runs one job at a time. Separate lanes never hold each other's work.
public sealed class TransferLane
{
    private sealed record Job(Func<CancellationToken, Task> Work, TaskCompletionSource Completion);
    private readonly object gate = new();
    private readonly Queue<Job> pending = new();
    private CancellationTokenSource? active;
    private bool running;
    public event Action? Changed;
    public int Count { get { lock (gate) return pending.Count + (active is not null ? 1 : 0); } }
    public Task Enqueue(Func<CancellationToken, Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool start;
        lock (gate)
        {
            pending.Enqueue(new(work, completion));
            start = !running;
            if (start) running = true;
        }
        Changed?.Invoke();
        if (start) _ = ProcessAsync();
        return completion.Task;
    }
    private async Task ProcessAsync()
    {
        while (true)
        {
            Job job;
            CancellationTokenSource source;
            lock (gate)
            {
                if (pending.Count == 0) { running = false; break; }
                job = pending.Dequeue();
                active = source = new();
            }
            Changed?.Invoke();
            try { await job.Work(source.Token); job.Completion.TrySetResult(); }
            catch (OperationCanceledException) { job.Completion.TrySetCanceled(); }
            catch (Exception ex) { job.Completion.TrySetException(ex); }
            finally
            {
                lock (gate) { active = null; }
                source.Dispose();
            }
        }
        Changed?.Invoke();
    }
    public void CancelAll()
    {
        lock (gate)
        {
            active?.Cancel();
            while (pending.TryDequeue(out var job)) job.Completion.TrySetCanceled();
        }
        Changed?.Invoke();
    }
}
