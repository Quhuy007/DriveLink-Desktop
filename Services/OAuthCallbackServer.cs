using System.Net;
using System.Net.Sockets;
using System.Text;
namespace DriveLink.Services;

public static class OAuthResultPage
{
    public static string Render(bool success, string detail)
    {
        var title = success ? "Đăng nhập thành công" : "Đăng nhập không thành công";
        var color = success ? "#168464" : "#d34655";
        var symbol = success ? "✓" : "!";
        return $$$"""
        <!doctype html><html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{{{title}}} · {{{AppBrand.WindowTitle}}}</title><link rel="icon" type="image/png" href="{{{AppBrand.WebIconDataUri}}}">
        <style>*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;background:#f3f6fc;color:#17253f;font-family:Segoe UI,Arial,sans-serif;padding:24px}.card{max-width:580px;width:100%;background:white;border:1px solid #e1e8f3;border-radius:24px;padding:36px 40px;box-shadow:0 20px 70px #17253f12}.brand{display:flex;align-items:center;gap:20px;margin-bottom:28px}.logo{width:120px;height:120px;object-fit:contain;flex-shrink:0}.name{font-size:30px;font-weight:700;color:#172b50}.credit{font-size:14px;color:#64748b;margin-top:7px}.tagline{font-size:12px;color:#718098;margin-top:10px}.status{display:inline-flex;align-items:center;gap:9px;color:{{{color}}};font-size:14px;font-weight:600}.icon{display:grid;place-items:center;width:28px;height:28px;border-radius:9px;background:{{{color}}};color:white;font-size:20px}h1{font-size:27px;line-height:1.3;margin:16px 0 12px}p{color:#64748b;line-height:1.7;overflow-wrap:anywhere}.footer{margin-top:28px;padding-top:20px;border-top:1px solid #edf0f6;font-size:13px}.version{display:block;margin-top:8px;font-size:12px;color:#8a97ac}@media(max-width:440px){.card{padding:24px}.logo{width:84px;height:84px}.brand{gap:14px}.name{font-size:26px}h1{font-size:23px}}</style></head>
        <body><main class="card"><div class="brand"><img class="logo" src="{{{AppBrand.WebIconDataUri}}}" alt="Icon DriveLink"><div><div class="name">DriveLink</div><div class="credit">Quang Huy · Phiên bản {{{AppBrand.Version}}}</div><div class="tagline">GỬI NHANH. NHẬN DỄ.</div></div></div><div class="status"><span class="icon">{{{symbol}}}</span>Kết nối Google</div><h1>{{{title}}}</h1><p>{{{WebUtility.HtmlEncode(detail)}}}</p><p class="footer">Bạn có thể đóng tab này và quay lại ứng dụng DriveLink.<span class="version">DriveLink · Quang Huy · v{{{AppBrand.Version}}}</span></p></main></body></html>
        """;
    }
}
public sealed class OAuthCallback(TcpClient client, NetworkStream stream, string? code, bool denied) : IDisposable
{
    public string? Code { get; } = code;
    public bool Denied { get; } = denied;
    public async Task RespondAsync(bool success, string detail, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(OAuthResultPage.Render(success, detail));
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; img-src data:; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct); await stream.WriteAsync(bytes, ct);
    }
    public void Dispose() { stream.Dispose(); client.Dispose(); }
}
public sealed class OAuthCallbackServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    public string RedirectUri { get; }
    public OAuthCallbackServer()
    {
        listener.Start();
        RedirectUri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
    }
    public async Task<OAuthCallback> WaitAsync(string state, CancellationToken ct)
    {
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync(ct);
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var first = await reader.ReadLineAsync(requestTimeout.Token) ?? "";
                var parts = first.Split(' ');
                var path = parts.ElementAtOrDefault(1) ?? "/";
                var uri = new Uri("http://127.0.0.1" + (path.StartsWith('/') && !path.StartsWith("//") ? path : "/"));
                var values = GoogleAuth.ParseQuery(uri.Query);
                int headerCount = 0;
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(requestTimeout.Token)))
                    if (++headerCount > 64) throw new IOException("Too many callback headers.");
                var callback = new OAuthCallback(client, stream, values.GetValueOrDefault("code"), values.ContainsKey("error"));
                if (parts[0] == "GET" && uri.AbsolutePath == "/" && values.GetValueOrDefault("state") == state)
                    return callback;
                using (callback) await callback.RespondAsync(false, "Không xác nhận được yêu cầu đăng nhập này. Hãy quay lại DriveLink và thử kết nối lại.", requestTimeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { client.Dispose(); }
            catch { client.Dispose(); throw; }
        }
    }
    public void Dispose() => listener.Stop();
}
