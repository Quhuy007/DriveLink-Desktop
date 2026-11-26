using DriveLink;
using DriveLink.Services;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

/// <summary>Kiểm tra phát hiện tệp mới qua nguồn thay đổi của Drive, dấu "đã nhận" và tên tệp trong Hộp thư đến.</summary>
static class InboxChecks
{
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    public static async Task Run(Action<bool, string> check, string root)
    {
        var folder = Path.Combine(root, "inbox"); Directory.CreateDirectory(folder);
        var account = new Account { Id = "acc", Email = "nhan@example.com" };
        var space = new Space { Id = "SPACE", Name = "[DriveLink] Làm việc" };
        var tokensAsked = new List<string>(); var patches = new List<string>();
        const string changes = """
            {"changes":[
              {"fileId":"f1","file":{"id":"f1","name":"bao-cao.pdf","mimeType":"application/pdf","size":"2048","parents":["SPACE"],"owners":[{"emailAddress":"gui@example.com","me":false}],"appProperties":{"dlSenderMachine":"MAY-GUI"}}},
              {"fileId":"f1","file":{"id":"f1","name":"bao-cao.pdf","mimeType":"application/pdf","size":"2048","parents":["SPACE"],"owners":[{"emailAddress":"gui@example.com","me":false}]}},
              {"fileId":"mine","file":{"id":"mine","name":"cua-toi.txt","mimeType":"text/plain","parents":["SPACE"],"owners":[{"emailAddress":"nhan@example.com","me":true}]}},
              {"fileId":"other","file":{"id":"other","name":"ngoai.txt","mimeType":"text/plain","parents":["ELSEWHERE"],"owners":[{"emailAddress":"x@example.com","me":false}]}},
              {"fileId":"bin","file":{"id":"bin","name":"da-xoa.txt","mimeType":"text/plain","parents":["SPACE"],"trashed":true,"owners":[{"emailAddress":"x@example.com","me":false}]}},
              {"fileId":"dir","file":{"id":"dir","name":"thu-muc","mimeType":"application/vnd.google-apps.folder","parents":["SPACE"],"owners":[{"emailAddress":"x@example.com","me":false}]}}
            ],"newStartPageToken":"T2"}
            """;
        using var http = new HttpClient(new ActionHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/changes/startPageToken")) return Json("{\"startPageToken\":\"T1\"}");
            if (path.EndsWith("/changes")) { tokensAsked.Add(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["pageToken"]!); return Json(tokensAsked.Count == 1 ? changes : "{\"changes\":[],\"newStartPageToken\":\"T3\"}"); }
            if (request.Method == HttpMethod.Patch) { patches.Add(await request.Content!.ReadAsStringAsync()); return path.EndsWith("/readonly") ? Json("{\"error\":{\"message\":\"no\"}}", HttpStatusCode.Forbidden) : Json("{\"id\":\"f1\"}"); }
            return Json("{}", HttpStatusCode.BadRequest);
        }));
        var drive = new DriveService(http, (_, _) => Task.FromResult("token"));
        var arrived = new List<IncomingFile>();
        var watcher = new InboxWatcher(drive, folder) { SpacesOf = _ => [space] };
        watcher.Arrived += arrived.Add;
        var first = await watcher.CheckAsync(account, CancellationToken.None);
        check(first == 0 && tokensAsked.Count == 0, "First check only records a starting point and does not report old files");
        var reopened = new InboxWatcher(drive, folder) { SpacesOf = _ => [space] };
        reopened.Arrived += arrived.Add;
        var found = await reopened.CheckAsync(account, CancellationToken.None);
        check(tokensAsked.SequenceEqual(["T1"]) && found == 1 && arrived.Single().File.Id == "f1" && arrived[0].Space.Id == "SPACE",
            "New files from others in a space are detected once, using the token saved before restart; own, trashed, folder and outside files are ignored");
        check(arrived[0].File.SenderMachine == "MAY-GUI" && arrived[0].File.DetailLabel.Contains("từ máy MAY-GUI"), "The sender computer name is shown with the file");
        await reopened.CheckAsync(account, CancellationToken.None);
        check(tokensAsked.SequenceEqual(["T1", "T2"]), "Each check continues from the latest change token");
        check(!File.ReadAllText(Path.Combine(folder, "watch.dat"), Encoding.Latin1).Contains("T2"), "Change tokens are stored encrypted");

        var marked = await drive.MarkReceivedAsync(account, "f1", CancellationToken.None);
        var denied = await drive.MarkReceivedAsync(account, "readonly", CancellationToken.None);
        check(marked && !denied && patches[0].Contains(ReceiptMarks.ThisMachineKey) && patches[0].Contains(Environment.MachineName),
            "Download marks the file as received by this computer, and quietly skips files it cannot edit");
        var withReceipts = new DriveItem { Name = "a.zip", AppProperties = new() { ["dlSenderMachine"] = "MAY-GUI", ["dlRcv_aaaa"] = "x", ["dlRcv_bbbb"] = "y" } };
        check(withReceipts.ReceivedCount == 2 && withReceipts.DetailLabel == "ZIP · từ máy MAY-GUI · 2 máy đã nhận", "Sender sees how many computers received the file");
        check(ReceiptMarks.Value(DateTimeOffset.UtcNow).Length + ReceiptMarks.ThisMachineKey.Length <= 124, "Receipt mark fits Google's 124-byte property limit");

        var inbox = Path.Combine(folder, "Hộp thư đến"); Directory.CreateDirectory(inbox);
        File.WriteAllText(Path.Combine(inbox, "bao-cao.pdf"), "");
        check(InboxWatcher.UniquePath(inbox, "bao-cao.pdf").EndsWith("bao-cao (1).pdf") && InboxWatcher.UniquePath(inbox, "a<b>.txt").EndsWith("a_b_.txt"),
            "Inbox downloads never overwrite an existing file and use safe file names");
        check(new UserSettings().ResolveInboxFolder().EndsWith(Path.Combine("Downloads", "DriveLink - Hộp thư đến")) && new UserSettings().AutoDownloadInbox && new UserSettings().NotifyNewFiles,
            "Inbox defaults to a DriveLink folder in Downloads with notifications and auto-download on");
    }
}
