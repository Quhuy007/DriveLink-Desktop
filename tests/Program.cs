using System.Net;
using System.Text;
using System.Text.Json;
using DriveLink;
using DriveLink.Services;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
if (args.Length == 2 && args[0] == "--update-feed")
{
    await UpdateChecks.VerifyPackage(args[1]);
    return;
}

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
var account = new Account { Id = "test", Email = "test@example.com" };
var root = Path.Combine(Path.GetTempPath(), "DriveLink-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new SecureStore(root);
    store.Save(new SavedState { Accounts = [new Account { Id = "1", RefreshToken = "sensitive-refresh-token" }] });
    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "accounts.dat"))).Contains("sensitive-refresh-token"), "Token is encrypted on disk");
    Check(store.Load().Accounts[0].RefreshToken == "sensitive-refresh-token", "Encrypted state can be restored for current Windows user");
    var pages = 0;
    using (var http = new HttpClient(new FakeHandler(async request =>
    {
        await Task.Yield();
        Check(request.Headers.Authorization?.Parameter == "token", "Authenticated Drive request");
        pages++;
        if (pages == 1) return Json("{\"nextPageToken\":\"page+/2\",\"files\":[{\"id\":\"a\",\"name\":\"A\",\"size\":\"123\"}]}");
        Check(request.RequestUri!.Query.Contains("pageToken=page%2B%2F2"), "Pagination token is encoded");
        return Json("{\"files\":[{\"id\":\"b\",\"name\":\"B\",\"size\":\"456\"}]}");
    })))
    {
        var service = new DriveService(http, (_, _) => Task.FromResult("token"));
        var rows = await service.ListFilesAsync(account, "folder'\\id", CancellationToken.None);
        Check(rows.Count == 2 && rows[1].Size == 456, "Lists every page and parses Drive string sizes");
    }
    var target = Path.Combine(root, "existing.txt");
    await File.WriteAllTextAsync(target, "original");
    using (var http = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"message\":\"denied\"}}") }))))
    {
        var service = new DriveService(http, (_, _) => Task.FromResult("token"));
        try { await service.DownloadAsync(account, new DriveItem { Id = "f", MimeType = "text/plain" }, target, CancellationToken.None); throw new Exception("Expected download error"); }
        catch (InvalidOperationException) { }
        Check(await File.ReadAllTextAsync(target) == "original", "Failed download preserves existing destination");
        Check(Directory.GetFiles(root, "*.partial").Length == 0, "Failed download leaves no partial files");
    }
    using (var http = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("received", Encoding.UTF8) }))))
    {
        var service = new DriveService(http, (_, _) => Task.FromResult("token"));
        await service.DownloadAsync(account, new DriveItem { Id = "f", MimeType = "text/plain" }, target, CancellationToken.None);
        Check(await File.ReadAllTextAsync(target) == "received", "Successful download replaces destination only after completion");
    }
    await File.WriteAllTextAsync(target, "original");
    using (var http = new HttpClient(new FakeHandler(_ =>
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentLength = 100;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    })))
    {
        var service = new DriveService(http, (_, _) => Task.FromResult("token"));
        try { await service.DownloadAsync(account, new DriveItem { Id = "f", MimeType = "text/plain" }, target, CancellationToken.None); throw new Exception("Expected truncated download error"); }
        catch (IOException) { }
        Check(await File.ReadAllTextAsync(target) == "original", "Truncated response cannot replace an existing file");
        Check(Directory.GetFiles(root, "*.partial").Length == 0, "Truncated response is cleaned up");
    }
    var upload = Path.Combine(root, "large.bin");
    await using (var file = File.Create(upload)) file.SetLength(8 * 1024 * 1024 + 13);
    var chunkCount = 0;
    using (var http = new HttpClient(new FakeHandler(async request =>
    {
        if (request.Method == HttpMethod.Post)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Location = new Uri("https://www.googleapis.com/upload/session-test");
            return response;
        }
        chunkCount++;
        var bytes = await request.Content!.ReadAsByteArrayAsync();
        if (chunkCount == 1)
        {
            Check(bytes.Length == 8 * 1024 * 1024 && request.Content.Headers.ContentRange!.From == 0, "Upload first chunk has correct size and range");
            var response = new HttpResponseMessage((HttpStatusCode)308);
            response.Headers.TryAddWithoutValidation("Range", "bytes=0-8388607");
            return response;
        }
        Check(bytes.Length == 13 && request.Content.Headers.ContentRange!.From == 8388608, "Upload final chunk has correct range");
        return Json("{\"id\":\"uploaded\"}");
    })))
    {
        var service = new DriveService(http, (_, _) => Task.FromResult("token"));
        var reports = new List<TransferProgress>();
        var uploadedId = await service.UploadAsync(account, "folder", upload, new InlineProgress<TransferProgress>(reports.Add), CancellationToken.None);
        Check(chunkCount == 2, "Large file uploads in bounded chunks");
        Check(uploadedId == "uploaded" && reports.Last().BytesTransferred == 8388621, "Upload returns the real Drive ID and measured byte count");
        Check(reports.Any(x => x.BytesPerSecond > 0), "Upload reports a measured transfer speed");
    }
    var attempts = 0;
    using (var http = new HttpClient(new FakeHandler(request =>
    {
        if (request.Method == HttpMethod.Post)
        {
            var response = Json("{}"); response.Headers.Location = new Uri("https://www.googleapis.com/upload/incomplete"); return Task.FromResult(response);
        }
        attempts++;
        var partial = new HttpResponseMessage((HttpStatusCode)308);
        partial.Headers.TryAddWithoutValidation("Range", "bytes=0-1023");
        return Task.FromResult(partial);
    })))
    {
        try { await new DriveService(http, (_, _) => Task.FromResult("token")).UploadAsync(account, "folder", upload, new Progress<TransferProgress>(), CancellationToken.None); throw new Exception("Expected partial acknowledgment error"); }
        catch (InvalidOperationException) { }
        Check(attempts == 1, "Partial upload acknowledgment stops without sending a wrong next chunk or retrying writes");
    }
    var zero = Path.Combine(root, "empty.txt"); File.WriteAllBytes(zero, []);
    using (var http = new HttpClient(new FakeHandler(request =>
    {
        if (request.Method == HttpMethod.Post)
        {
            var response = Json("{}"); response.Headers.Location = new Uri("https://www.googleapis.com/upload/empty"); return Task.FromResult(response);
        }
        Check(request.Content!.Headers.ContentRange!.ToString() == "bytes */0", "Empty upload uses a valid zero-length range");
        return Task.FromResult(Json("{\"id\":\"empty\"}"));
    })))
    {
        await new DriveService(http, (_, _) => Task.FromResult("token")).UploadAsync(account, "folder", zero, new Progress<TransferProgress>(), CancellationToken.None);
    }
    var historyFile = Path.Combine(root, "history.txt");
    File.WriteAllText(historyFile, "history");
    var history = new LocalFileHistory(store.Load(), store);
    history.Record("drive-id", historyFile);
    var restoredHistory = new LocalFileHistory(store.Load(), store);
    Check(restoredHistory.FindExisting("drive-id") == historyFile, "Local file path survives application restart");
    Check(restoredHistory.FindExisting("cloud-only") is null, "Cloud-only file is never mistaken for a local file");
    File.Move(historyFile, Path.Combine(root, "moved.txt"));
    Check(restoredHistory.FindExisting("drive-id") is null, "Moved local file is detected instead of opening a wrong folder");
    var receiving = new TransferLane(); var sending = new TransferLane();
    var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var receiveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseReceive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
    {
        var sendJob = sending.Enqueue(async ct => { sendStarted.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
        var queuedSendRan = false;
        var queuedSend = sending.Enqueue(_ => { queuedSendRan = true; return Task.CompletedTask; });
        var receiveJob = receiving.Enqueue(async ct => { receiveStarted.SetResult(); await releaseReceive.Task.WaitAsync(ct); });
        await Task.WhenAll(sendStarted.Task, receiveStarted.Task).WaitAsync(limit.Token);
        Check(sending.Count == 2 && receiving.Count == 1, "Upload and download can be active simultaneously with independent queues");
        sending.CancelAll();
        try { await sendJob; } catch (OperationCanceledException) { }
        try { await queuedSend; } catch (OperationCanceledException) { }
        Check(!receiveJob.IsCompleted && !queuedSendRan, "Canceling upload clears its queue without canceling download");
        releaseReceive.SetResult(); await receiveJob.WaitAsync(limit.Token);
    }
    var order = new List<int>();
    await Task.WhenAll(receiving.Enqueue(async _ => { await Task.Delay(10); order.Add(1); }), receiving.Enqueue(_ => { order.Add(2); return Task.CompletedTask; }));
    Check(order.SequenceEqual(new[] { 1, 2 }), "One transfer direction preserves queue order");
    Check(new DriveItem { Id = "id", MimeType = "application/vnd.google-apps.folder" }.BrowserUrl == "https://drive.google.com/drive/folders/id", "Folder web link uses folder route");
    Check(!OAuthResultPage.Render(false, "<script>alert(1)</script>").Contains("<script>"), "Login result escapes supplied text");
    using (var server = new OAuthCallbackServer())
    using (var localHttp = new HttpClient())
    using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
    {
        var waiting = server.WaitAsync("expected-state", limit.Token);
        var invalid = await localHttp.GetStringAsync(server.RedirectUri + "?state=wrong&code=invalid", limit.Token);
        Check(invalid.Contains("Đăng nhập không thành công") && !waiting.IsCompleted, "Wrong OAuth state shows failure and does not complete sign-in");
        var request = localHttp.GetAsync(server.RedirectUri + "?state=expected-state&code=valid-code", limit.Token);
        using var callback = await waiting;
        Check(callback.Code == "valid-code" && !request.IsCompleted, "Browser does not show success before application confirms authentication");
        await callback.RespondAsync(true, "Đã kết nối.", limit.Token);
        using var response = await request;
        var html = await response.Content.ReadAsStringAsync(limit.Token);
        Check(html.Contains("Đăng nhập thành công") && response.Content.Headers.ContentType?.MediaType == "text/html", "OAuth callback serves a UTF-8 HTML success page");
        Check(response.Headers.CacheControl?.NoStore == true, "OAuth result is not cached");
    }
    using (var server = new OAuthCallbackServer())
    using (var localHttp = new HttpClient())
    using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
    {
        var waiting = server.WaitAsync("state", limit.Token);
        var request = localHttp.GetAsync(server.RedirectUri + "?state=state&error=access_denied", limit.Token);
        using var callback = await waiting;
        Check(callback.Denied, "OAuth permission denial is distinguished from successful login");
        await callback.RespondAsync(false, "Bạn chưa cấp quyền.", limit.Token);
        using var response = await request;
        Check((await response.Content.ReadAsStringAsync(limit.Token)).Contains("Đăng nhập không thành công"), "Denied login displays the failure page");
    }
    foreach (var success in new[] { true, false })
    {
        var html = OAuthResultPage.Render(success, "Trạng thái minh họa.");
        Check(html.Contains("Quang Huy") && html.Contains("v" + AppBrand.Version), "Browser result includes author and release version");
        Check(html.Contains("img-src") == false && html.Contains("data:image/png;base64,") && html.Contains("object-fit:contain"), "Browser result embeds the full icon without external assets or cropping");
    }
    var preferences = new Dictionary<string, SpacePreference> { ["a"] = new() { Color = "#168464" }, ["z"] = new() { Pinned = true, Color = "#8657D6" }, ["hidden"] = new() { Hidden = true } };
    var organized = SpaceOrganizer.Apply(new[] { new Space { Id = "a", Name = "A" }, new Space { Id = "z", Name = "Z" }, new Space { Id = "hidden", Name = "H" } }, preferences);
    Check(organized.Count == 2 && organized[0].Id == "z", "Pinned workspaces sort first and hidden workspaces are omitted");
    Check(organized[0].Color == "#8657D6" && organized[1].Color == "#168464", "Workspace colors are applied without changing Google data");
    var preferenceState = store.Load(); preferenceState.SpacePreferences = preferences; preferenceState.Settings = new() { ArchiveFormat = "rar", DropOverlayOpacity = 0.35, HighCompression = true };
    store.Save(preferenceState);
    var restoredPreferences = store.Load();
    Check(restoredPreferences.SpacePreferences["z"].Pinned && restoredPreferences.Settings.ArchiveFormat == "rar" && restoredPreferences.Settings.DropOverlayOpacity == 0.35, "Workspace pins, archive preferences and overlay opacity survive restart");
    var olderAccountData = Encoding.UTF8.GetBytes("{\"Accounts\":[],\"LocalFiles\":{}}");
    File.WriteAllBytes(Path.Combine(root, "accounts.dat"), ProtectedData.Protect(olderAccountData, null, DataProtectionScope.CurrentUser));
    Check(store.Load().SpacePreferences["z"].Pinned && store.Load().Settings.ArchiveFormat == "rar", "An older application writing accounts cannot erase new workspace preferences");
    var oldState = JsonSerializer.Deserialize<SavedState>("{\"Accounts\":[],\"LocalFiles\":{}}")!;
    Check(oldState.Settings.ArchiveFormat == "zip" && !oldState.Settings.RunInBackground && !oldState.Settings.StartWithWindows, "Older saved accounts receive compatible settings with background/startup off");
    Check(StartupRegistration.BuildCommand(@"C:\Apps With Spaces\DriveLink.exe") == "\"C:\\Apps With Spaces\\DriveLink.exe\" --background", "Startup command quotes paths and supports background launch");
    using (var http = new HttpClient(new FakeHandler(async request =>
    {
        Check(request.Method == HttpMethod.Patch && request.RequestUri!.AbsolutePath.EndsWith("/folder"), "Workspace deletion uses PATCH, not permanent DELETE");
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Check(body.RootElement.GetProperty("trashed").GetBoolean(), "Workspace removal moves to Drive trash");
        return Json("{\"id\":\"folder\",\"trashed\":true}");
    }))) await new DriveService(http, (_, _) => Task.FromResult("token")).TrashSpaceAsync(account, "folder", CancellationToken.None);
    var sourceFolder = Path.Combine(root, "Thư mục kiểm tra có dấu"); Directory.CreateDirectory(sourceFolder);
    Directory.CreateDirectory(Path.Combine(sourceFolder, "trống")); Directory.CreateDirectory(Path.Combine(sourceFolder, "con"));
    var originalText = string.Concat(Enumerable.Repeat("DriveLink Quang Huy kiểm tra nén file.\n", 3000));
    File.WriteAllText(Path.Combine(sourceFolder, "con", "tài liệu.txt"), originalText);
    File.WriteAllText(Path.Combine(sourceFolder, ".hidden-test"), "fixture");
    var compressor = new FolderCompressor(new(null, null, null), Path.Combine(root, "archives"));
    string packedPath;
    using (var packed = await compressor.PackAsync(sourceFolder, new UserSettings { CompressionTool = "builtin" }, null, CancellationToken.None))
    {
        packedPath = packed.ArchivePath;
        using var archive = ZipFile.OpenRead(packed.ArchivePath);
        var nested = archive.GetEntry("Thư mục kiểm tra có dấu/con/tài liệu.txt");
        Check(nested is not null && archive.GetEntry("Thư mục kiểm tra có dấu/trống/") is not null, "Folder ZIP preserves Unicode paths and empty subfolders");
        using var reader = new StreamReader(nested!.Open());
        Check(await reader.ReadToEndAsync() == originalText, "Folder ZIP preserves full file contents");
        Check(packed.ArchiveBytes < packed.OriginalBytes, "Repetitive text compresses to a smaller archive");
    }
    Check(!File.Exists(packedPath) && File.ReadAllText(Path.Combine(sourceFolder, "con", "tài liệu.txt")) == originalText, "Temporary archive is removed while source folder stays intact");
    history.Record("folder-upload", sourceFolder);
    Check(history.FindExisting("folder-upload") == sourceFolder, "Sent folder history points to the original folder, not deleted temporary ZIP");
    using (var canceledPack = new CancellationTokenSource())
    {
        canceledPack.Cancel();
        try { using var ignored = await compressor.PackAsync(sourceFolder, new(), null, canceledPack.Token); throw new Exception("Expected pack cancellation"); } catch (OperationCanceledException) { }
        Check(!Directory.EnumerateFiles(Path.Combine(root, "archives"), "*", SearchOption.AllDirectories).Any(), "Canceled compression does not leave an archive to upload");
    }
    var installedTools = CompressionTools.Detect();
    Console.WriteLine(installedTools.Summary);
    if (installedTools.WinRar is not null)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var packed = await new FolderCompressor(installedTools, Path.Combine(root, "external")).PackAsync(sourceFolder, new() { ArchiveFormat = "zip", CompressionTool = "winrar" }, null, limit.Token);
        using var archive = ZipFile.OpenRead(packed.ArchivePath);
        Check(archive.Entries.Any(e => e.FullName.EndsWith("tài liệu.txt")), "Installed WinRAR creates a valid ZIP with Unicode folder contents");
    }
    if (installedTools.CanCreateRar)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var packed = await new FolderCompressor(installedTools, Path.Combine(root, "external")).PackAsync(sourceFolder, new() { ArchiveFormat = "rar", CompressionTool = "winrar", HighCompression = true }, null, limit.Token);
        var signature = new byte[8]; using (var input = File.OpenRead(packed.ArchivePath)) input.ReadExactly(signature);
        Check(signature.Take(6).SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1a, 0x07 }), "Installed WinRAR creates a real RAR archive");
    }
    try { await ShellFileNavigator.OpenContainingFolderAsync(Path.Combine(root, "nonexistent.txt")); throw new Exception("Expected missing file"); } catch (FileNotFoundException) { Check(true, "Folder navigation detects missing files off the UI thread"); }
    var uiDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await CloudChecks.Run(Check, store);
    await UpdateChecks.Run(Check, store);
    var uiThread = new Thread(() =>
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resourceSource = System.Xml.Linq.XDocument.Load(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "App.xaml")));
        System.Xml.Linq.XNamespace wpfNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resourceRoot = new System.Xml.Linq.XElement(wpfNamespace + "ResourceDictionary",
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "local", "clr-namespace:DriveLink;assembly=DriveLink"),
            resourceSource.Root!.Element(wpfNamespace + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resourceRoot.ToString());
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try
            {
                Check(AppBrand.Icon.PixelWidth > 0, "Application icon is embedded and readable");
                var startupBefore = StartupRegistration.IsEnabled();
                var options = new SettingsWindow(new SavedState(), store); options.Show();
                var settingsGrid = (Grid)options.Content;
                Check(settingsGrid.Children.OfType<TabControl>().Single().Items.Count == 4, "Settings includes interface/system, compression, accounts and updates");
                var updateFlow = new AppUpdateFlow(new UpdateFixture());
                await updateFlow.CheckAsync(CancellationToken.None);
                var updates = new UpdateWindow(updateFlow, () => false, () => { }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowInTaskbar = false };
                updates.Show(); updates.UpdateLayout();
                var updatePanel = (StackPanel)((ScrollViewer)updates.Content).Content;
                Check(updatePanel.Children.OfType<TextBlock>().Any(x => x.Text.Contains("0.9.0")), "Update window clearly displays current and available versions");
                Check(updatePanel.Children.OfType<Button>().Any(x => (string)x.Content == "Tải và cập nhật" && x.IsEnabled), "Update action is available after a release is found");
                updates.Close();
                options.Close();
                var cloudWindow = new CloudConfigWindow(new SavedState(), store, (_, _) => throw new Exception("Opening cloud must not apply configuration"), autoLoad: false);
                cloudWindow.Show();
                var cloudTabs = ((Grid)cloudWindow.Content).Children.OfType<TabControl>().Single();
                Check(cloudTabs.Items.Count == 2 && cloudWindow.Title.StartsWith("Cloud lấy OAuth"), "Cloud OAuth window opens directly in receiving mode without a reader login form");
                var receiver = (StackPanel)((ScrollViewer)((TabItem)cloudTabs.Items[0]).Content).Content;
                var unlockField = receiver.Children.OfType<PasswordBox>().Single();
                var downloadButton = receiver.Children.OfType<Button>().Single(button => button.Content.ToString()!.StartsWith("Tải,"));
                downloadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(unlockField.BorderBrush == System.Windows.Media.Brushes.Firebrick && unlockField.BorderThickness.Left == 2, "Missing OAuth password highlights its input with a red border before any network request");
                var header = ((Grid)cloudWindow.Content).Children.OfType<StackPanel>().First();
                var banner = header.Children.OfType<Border>().Single();
                Check(((TextBlock)banner.Child).Foreground == System.Windows.Media.Brushes.DarkRed, "Password error is also shown prominently in the top status banner");
                if (args.Contains("--export-previews"))
                {
                    var previewDir = Path.Combine(AppContext.BaseDirectory, "login-previews"); Directory.CreateDirectory(previewDir);
                    for (var tabIndex = 0; tabIndex < cloudTabs.Items.Count; tabIndex++)
                    {
                        cloudTabs.SelectedIndex = tabIndex; cloudWindow.UpdateLayout();
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)cloudWindow.ActualWidth, (int)cloudWindow.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(cloudWindow);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var previewFile = File.Create(Path.Combine(previewDir, $"cloud-{tabIndex}.png")); encoder.Save(previewFile);
                    }
                }
                var adminTab = (TabItem)cloudTabs.Items[1];
                Check(adminTab.Visibility == Visibility.Collapsed, "Administration entry remains hidden before admin verification and shortcut");
                Check(!((StackPanel)((ScrollViewer)adminTab.Content).Content).IsEnabled, "Cloud administration is disabled before authentication");
                unlockField.Password = "retry";
                Check(unlockField.BorderThickness.Left == 1 && unlockField.BorderBrush != System.Windows.Media.Brushes.Firebrick, "Editing password clears the input warning for the next attempt");
                cloudWindow.Close();
                var cloudFixture = new CloudConnection { ProjectUrl = "https://drivelink-test.supabase.co", PublishableKey = "sb_publishable_fixture_only" };
                foreach (var mode in new[] { "wrong", "reader", "admin" })
                {
                    using var adminHttp = new HttpClient(new FakeHandler(async request =>
                    {
                        if (request.RequestUri!.AbsolutePath == "/auth/v1/token")
                        {
                            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                            Check(body.RootElement.GetProperty("email").GetString() == "admin_account_verify@drivelink.private.com", "Password-only admin prompt uses the configured surviving admin account");
                            return mode == "wrong" ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"code\":400,\"error_code\":\"invalid_credentials\"}") } : Json("{\"access_token\":\"fixture\",\"expires_in\":3600,\"user\":{\"id\":\"00000000-0000-0000-0000-000000000001\"}}");
                        }
                        return Json("[{\"role\":\"" + mode + "\"}]");
                    }));
                    var dialog = new AdminSignInWindow(() => new SupabaseConfigService(cloudFixture, adminHttp));
                    dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -10000; dialog.Top = -10000; dialog.ShowInTaskbar = false;
                    Exception? dialogError = null;
                    dialog.Loaded += (_, _) =>
                    {
                    _ = Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                        var panel = (StackPanel)dialog.Content; var field = panel.Children.OfType<PasswordBox>().Single(); var submit = panel.Children.OfType<Button>().Single();
                        Check(!panel.Children.OfType<TextBox>().Any(), "Admin prompt contains no account or email input");
                        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(dialog.IsVisible && field.BorderBrush == System.Windows.Media.Brushes.Firebrick, "Empty admin password keeps prompt open and marks password red");
                        field.Password = "fixture-password"; submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        catch (Exception ex) { dialogError = ex; dialog.Close(); }
                    });
                    };
                    var result = dialog.ShowDialog();
                    if (dialogError is not null) throw dialogError;
                    Check(!dialog.IsVisible, "Admin password prompt closes after an authentication result");
                    if (mode == "admin") Check(result == true && dialog.Session?.IsAdmin == true, "Configured admin opens administration with its password and server-verified role");
                    else Check(result != true && dialog.Session is null && dialog.Failure.Contains(mode == "wrong" ? "sai mật khẩu" : "quyền quản trị"), "Wrong password or reader role cannot open administration");
                    dialog.Session?.Dispose();
                }
                Check(StartupRegistration.IsEnabled() == startupBefore, "Opening and canceling settings does not register Windows startup");
                using (var tray = new TrayController(() => { }, () => { }, () => { })) Check(!tray.Visible, "Tray icon remains hidden until background mode is enabled");
                foreach (var success in new[] { true, false })
                {
                    var login = new LoginStatusWindow(() => { });
                    var didClose = false; login.Closed += (_, _) => didClose = true;
                    login.Show();
                    Check(((TextBlock)login.FindName("StateTitle")).Text.Contains("Đang chờ"), "Small login window starts in waiting state");
                    var watch = Stopwatch.StartNew();
                    var completion = login.CompleteAsync(success, "Kết quả kiểm tra, không đăng nhập Google thật.");
                    var title = ((TextBlock)login.FindName("StateTitle")).Text;
                    Check(title.Contains(success ? "✓ Đăng nhập thành công" : "! Đăng nhập không thành công"), "Small login window shows the correct result");
                    await completion;
                    Check(didClose && watch.Elapsed.TotalSeconds >= 2.8 && watch.Elapsed.TotalSeconds < 7, "Login result window closes automatically after 3 seconds");
                }
                var canceled = false;
                var pending = new LoginStatusWindow(() => canceled = true); pending.Show(); pending.Close();
                Check(canceled && pending.IsVisible, "Closing a waiting login window requests cancellation instead of hiding the result");
                await pending.CompleteAsync(false, "Đã hủy.");
                uiDone.SetResult();
            }
            catch (Exception ex) { uiDone.SetException(ex); }
            finally { app.Shutdown(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        });
        Dispatcher.Run();
    });
    uiThread.SetApartmentState(ApartmentState.STA); uiThread.Start();
    await uiDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
    if (args.Contains("--export-previews"))
    {
        var preview = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "login-previews")); Directory.CreateDirectory(preview);
        File.WriteAllText(Path.Combine(preview, "success.html"), OAuthResultPage.Render(true, "Tài khoản Google đã được liên kết. Đây là trang xem thử giao diện."));
        File.WriteAllText(Path.Combine(preview, "failure.html"), OAuthResultPage.Render(false, "Chưa hoàn tất cấp quyền Google. Đây là trang xem thử giao diện."));
        Console.WriteLine("Login previews: " + preview);
    }
    Console.WriteLine($"All {passed} checks passed. No real Google or Supabase requests were sent.");
}
finally
{
    // This unique directory contains only test fixtures and generated archives.
    var resolvedRoot = Path.GetFullPath(root);
    var allowedPrefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "DriveLink-checks-");
    if (!resolvedRoot.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
    Directory.Delete(resolvedRoot, true);
}
static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
}
sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
