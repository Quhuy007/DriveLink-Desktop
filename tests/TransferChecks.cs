using DriveLink;
using DriveLink.Services;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

/// <summary>Kiểm tra truyền tệp khi mạng chập chờn: tự thử lại, gửi tiếp từ chỗ Google đã nhận, tải tiếp bằng Range, đối chiếu MD5.</summary>
static class TransferChecks
{
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Resume(long last)
    {
        var response = new HttpResponseMessage((HttpStatusCode)308);
        if (last >= 0) response.Headers.TryAddWithoutValidation("Range", $"bytes=0-{last}");
        return response;
    }
    private static DriveService Service(HttpClient http) => new(http, (_, _) => Task.FromResult("token")) { Delay = (_, _) => Task.CompletedTask };

    public static async Task Run(Action<bool, string> check, string root, Account account)
    {
        var folder = Path.Combine(root, "transfers"); Directory.CreateDirectory(folder);
        var upload = Path.Combine(folder, "video.bin");
        var data = new byte[DriveService.ChunkSize + 4096];
        Random.Shared.NextBytes(data);
        await File.WriteAllBytesAsync(upload, data);

        // 1. Mạng rớt giữa khối đầu: hỏi Google đã nhận tới đâu rồi gửi tiếp đúng từ đó.
        var ranges = new List<string>(); var posts = 0; var failed = false; var retries = new List<int>();
        using (var http = new HttpClient(new ActionHandler(async request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; var r = Json("{}"); r.Headers.Location = new Uri("https://www.googleapis.com/upload/s1"); return r; }
            var range = request.Content!.Headers.ContentRange!;
            var body = await request.Content.ReadAsByteArrayAsync();
            ranges.Add(range.HasRange ? $"{range.From}-{range.To}" : "*");
            if (!failed && range.HasRange) { failed = true; throw new HttpRequestException("network down"); }
            if (!range.HasRange) return Resume(4_194_303); // Google đã nhận nửa khối đầu.
            if (range.To < data.Length - 1) return Resume(range.To!.Value);
            return Json("{\"id\":\"done-1\"}");
        })))
        {
            UploadCheckpoint? saved = null;
            var id = await Service(http).UploadAsync(account, "folder", upload, new Progress<TransferProgress>(), CancellationToken.None,
                new UploadOptions { Checkpoint = c => saved = c, Retrying = (n, _) => retries.Add(n) });
            check(id == "done-1" && posts == 1 && ranges.SequenceEqual(["0-8388607", "*", "4194304-8392703"]) && retries.SequenceEqual([1]),
                "Upload retries after a network drop and resumes from the offset Google confirmed");
            check(saved is { SessionUri: "https://www.googleapis.com/upload/s1" } && saved.Length == data.Length, "Upload session is checkpointed so it can resume after the app closes");
        }

        // 2. Mở lại app: dùng phiên đã lưu, chỉ gửi phần còn thiếu, không tạo phiên mới.
        var checkpoint = new UploadCheckpoint { SessionUri = "https://www.googleapis.com/upload/s2", Length = data.Length, LastWriteUtc = File.GetLastWriteTimeUtc(upload) };
        ranges.Clear(); posts = 0;
        using (var http = new HttpClient(new ActionHandler(request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; var r = Json("{}"); r.Headers.Location = new Uri("https://www.googleapis.com/upload/new"); return Task.FromResult(r); }
            var range = request.Content!.Headers.ContentRange!;
            ranges.Add(range.HasRange ? $"{range.From}-{range.To}" : "*");
            return Task.FromResult(range.HasRange ? Json("{\"id\":\"done-2\"}") : Resume(DriveService.ChunkSize - 1));
        })))
        {
            var id = await Service(http).UploadAsync(account, "folder", upload, new Progress<TransferProgress>(), CancellationToken.None, new UploadOptions { Resume = checkpoint });
            check(id == "done-2" && posts == 0 && ranges.SequenceEqual(["*", $"{DriveService.ChunkSize}-{data.Length - 1}"]), "Saved upload session continues after restart with only the missing bytes");
        }

        // 3. Phiên đã hết hạn hoặc tệp đã bị sửa: bắt đầu phiên mới.
        ranges.Clear(); posts = 0;
        using (var http = new HttpClient(new ActionHandler(request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; var r = Json("{}"); r.Headers.Location = new Uri("https://www.googleapis.com/upload/s3"); return Task.FromResult(r); }
            var range = request.Content!.Headers.ContentRange!;
            ranges.Add(range.HasRange ? $"{range.From}-{range.To}" : "*");
            if (!range.HasRange) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(range.To < data.Length - 1 ? Resume(range.To!.Value) : Json("{\"id\":\"done-3\"}"));
        })))
        {
            var id = await Service(http).UploadAsync(account, "folder", upload, new Progress<TransferProgress>(), CancellationToken.None, new UploadOptions { Resume = checkpoint });
            var changed = await Service(http).UploadAsync(account, "folder", upload, new Progress<TransferProgress>(), CancellationToken.None,
                new UploadOptions { Resume = new UploadCheckpoint { SessionUri = checkpoint.SessionUri, Length = data.Length + 1, LastWriteUtc = checkpoint.LastWriteUtc } });
            check(id == "done-3" && changed == "done-3" && posts == 2 && ranges[0] == "*" && ranges.Count(x => x == "*") == 1, "Expired session or changed file starts a fresh upload");
        }

        // 4. Không thử lại lỗi không tạm thời (hết dung lượng / không có quyền).
        var denied = 0;
        using (var http = new HttpClient(new ActionHandler(request => { denied++; return Task.FromResult(Json("{\"error\":{\"message\":\"quota\"}}", HttpStatusCode.Forbidden)); })))
        {
            var thrown = false;
            try { await Service(http).UploadAsync(account, "folder", upload, new Progress<TransferProgress>(), CancellationToken.None); } catch (DriveApiException) { thrown = true; }
            check(thrown && denied == 1, "Permission or quota errors are not retried");
        }

        // 5. Tải về: mạng rớt giữa chừng thì tải tiếp bằng Range, đối chiếu MD5 rồi mới thay tệp đích.
        var content = Encoding.UTF8.GetBytes("DriveLink resumable download content");
        var md5 = Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();
        var item = new DriveItem { Id = "file-1", Name = "a.txt", MimeType = "text/plain", Size = content.Length, Md5 = md5 };
        var target = Path.Combine(folder, "a.txt");
        var requests = new List<string>(); retries.Clear();
        using (var http = new HttpClient(new ActionHandler(request =>
        {
            var from = request.Headers.Range?.Ranges.First().From;
            requests.Add(from?.ToString() ?? "full");
            if (from is null)
            {
                var cut = new ByteArrayContent(content[..10]); cut.Headers.ContentLength = content.Length; // Mạng ngắt: báo đủ dung lượng nhưng chỉ nhận được 10 byte đầu.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = cut });
            }
            var rest = new ByteArrayContent(content[(int)from.Value..]);
            rest.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from.Value, content.Length - 1, content.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = rest });
        })))
        {
            await Service(http).DownloadAsync(account, item, target, CancellationToken.None, null, new DownloadOptions { Retrying = (n, _) => retries.Add(n) });
            check((await File.ReadAllBytesAsync(target)).SequenceEqual(content) && requests.SequenceEqual(["full", "10"]) && retries.SequenceEqual([1]),
                "Download resumes with a Range request after a drop and verifies MD5");
            check(!File.Exists(DriveService.PartialPath(target, item)), "Completed download leaves no partial file");
        }

        // 6. Mở lại app: phần đã tải trước đó được dùng tiếp.
        File.Delete(target);
        await File.WriteAllBytesAsync(DriveService.PartialPath(target, item), content[..20]);
        requests.Clear();
        using (var http = new HttpClient(new ActionHandler(request =>
        {
            var from = request.Headers.Range?.Ranges.First().From ?? 0;
            requests.Add(from.ToString());
            var rest = new ByteArrayContent(content[(int)from..]);
            rest.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, content.Length - 1, content.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = rest });
        })))
        {
            await Service(http).DownloadAsync(account, item, target, CancellationToken.None);
            check((await File.ReadAllBytesAsync(target)).SequenceEqual(content) && requests.SequenceEqual(["20"]), "Download continues from the bytes saved before the app closed");
        }

        // 7. Dữ liệu sai MD5: tải lại từ đầu một lần; vẫn sai thì báo lỗi, không đụng tệp đích.
        await File.WriteAllTextAsync(target, "original");
        var served = 0;
        using (var http = new HttpClient(new ActionHandler(_ =>
        {
            served++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("DriveLink resumable download CORRUPT")) });
        })))
        {
            var thrown = false;
            try { await Service(http).DownloadAsync(account, item, target, CancellationToken.None); } catch (IOException) { thrown = true; }
            check(thrown && served == 2 && await File.ReadAllTextAsync(target) == "original" && !File.Exists(DriveService.PartialPath(target, item)),
                "MD5 mismatch is retried once, then fails without replacing the destination");
        }

        // 8. Tắt app giữa chừng: giữ phần đã tải để lần sau tiếp tục.
        using var stop = new CancellationTokenSource();
        using (var http = new HttpClient(new ActionHandler(_ =>
        {
            stop.Cancel();
            throw new TaskCanceledException();
        })))
        {
            await File.WriteAllBytesAsync(DriveService.PartialPath(target, item), content[..5]);
            try { await Service(http).DownloadAsync(account, item, target, stop.Token, null, new DownloadOptions { KeepPartialOnCancel = true }); } catch (OperationCanceledException) { }
            check(File.Exists(DriveService.PartialPath(target, item)), "Closing the app keeps the partial download for later");
        }
        await RunCenter(check, root, account);
        check(DriveService.IsTransient(new HttpRequestException(), CancellationToken.None) && DriveService.IsTransient(new DriveApiException((HttpStatusCode)503, "busy"), CancellationToken.None)
            && !DriveService.IsTransient(new DriveApiException(HttpStatusCode.Forbidden, "no"), CancellationToken.None), "Only network, timeout, 429 and 5xx errors are treated as temporary");
    }

    private static async Task RunCenter(Action<bool, string> check, string root, Account account)
    {
        // Hàng song song: tối đa 3 việc cùng lúc, việc chờ hủy riêng được.
        var limit = 3;
        var lane = new TransferLane(() => limit);
        var gates = Enumerable.Range(0, 5).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var started = 0;
        var cancels = new Action[5];
        var jobs = Enumerable.Range(0, 5).Select(i => lane.Enqueue(async ct => { Interlocked.Increment(ref started); await gates[i].Task.WaitAsync(ct); }, out cancels[i])).ToArray();
        await Task.Delay(100);
        check(lane.Running == 3 && lane.Waiting == 2 && started == 3, "Transfer lane runs up to the adaptive limit in parallel");
        cancels[4]();
        check(lane.Waiting == 1 && jobs[4].IsCanceled, "A waiting transfer can be canceled on its own");
        limit = 4; lane.Pump(); await Task.Delay(100);
        check(lane.Running == 4 && started == 4, "Raising the limit starts more waiting transfers");
        foreach (var gate in gates) gate.TrySetResult();
        await Task.WhenAll(jobs.Take(4));

        // Tự điều chỉnh 2–6 luồng.
        var tuner = new AdaptiveConcurrency();
        var now = DateTimeOffset.Parse("2026-11-26T00:00:00Z");
        tuner.Sample(1_000_000, true, now); tuner.Sample(1_500_000, true, now += TimeSpan.FromSeconds(10));
        var raised = tuner.Current;
        tuner.ReportRetry(now); var lowered = tuner.Current;
        tuner.Sample(9_000_000, true, now += TimeSpan.FromSeconds(10));
        var calm = tuner.Current;
        for (var i = 0; i < 10; i++) tuner.Sample(1_000_000 * (i + 2) * 2, true, now += TimeSpan.FromSeconds(31));
        var top = tuner.Current;
        for (var i = 0; i < 10; i++) tuner.ReportRetry(now += TimeSpan.FromSeconds(1));
        check(raised == 5 && lowered == 4 && calm == 4 && top == 6 && tuner.Current == 2, "Parallel transfers grow while throughput improves (max 6) and shrink on network retries (min 2)");

        // Trung tâm truyền tải: tải lên xong ghi Hoạt động, ghi tên máy gửi, không để lại lượt chưa xong.
        var folder = Path.Combine(root, "center"); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "report.pdf"); await File.WriteAllBytesAsync(file, new byte[2048]);
        string? startBody = null;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdUploads = false;
        using var http = new HttpClient(new CancellableHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post) { startBody = await request.Content!.ReadAsStringAsync(); var r = Json("{}"); r.Headers.Location = new Uri("https://www.googleapis.com/upload/c1"); return r; }
            if (holdUploads) await hold.Task.WaitAsync(ct);
            return Json("{\"id\":\"sent-1\"}");
        }));
        var drive = new DriveService(http, (_, _) => Task.FromResult("token")) { Delay = (_, _) => Task.CompletedTask };
        var space = new Space { Id = "space", Name = "[DriveLink] Làm việc" };
        var center = new TransferCenter(drive, new FolderCompressor(), () => new UserSettings(), folder);
        var finished = new TaskCompletionSource<TransferItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        center.Completed += item => finished.TrySetResult(item);
        center.EnqueueUpload(account, space, file);
        var done = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check(done.State == TransferState.Done && done.FileId == "sent-1" && center.Unfinished().Count == 0 && center.Activity[0].Result == TransferState.Done && center.Activity[0].Name == "report.pdf",
            "Finished upload is recorded in Activity and leaves nothing to resume");
        check(startBody is not null && startBody.Contains("dlSenderMachine") && startBody.Contains(Environment.MachineName), "Upload records the sending computer's name on the file");
        check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "activity.dat"))).Contains("report.pdf"), "Activity history is encrypted on disk");

        // Tắt app giữa chừng: lượt gửi được giữ lại (kèm phiên tải lên) để lần mở sau tiếp tục.
        holdUploads = true;
        var item2 = center.EnqueueUpload(account, space, file);
        await Task.Delay(200);
        center.Shutdown();
        await Task.Delay(200);
        var reopened = new TransferCenter(drive, new FolderCompressor(), () => new UserSettings(), folder);
        var left = reopened.Unfinished();
        check(item2.State == TransferState.Interrupted && left.Count == 1 && left[0].SourcePath == file && left[0].Checkpoint?.SessionUri == "https://www.googleapis.com/upload/c1",
            "Closing the app keeps unfinished uploads with their Google session for the next start");
        reopened.Discard(left[0]);
        check(new TransferCenter(drive, new FolderCompressor(), () => new UserSettings(), folder).Unfinished().Count == 0, "Declining to resume clears the saved transfer");
        hold.TrySetResult();
    }

    private sealed class CancellableHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
}
