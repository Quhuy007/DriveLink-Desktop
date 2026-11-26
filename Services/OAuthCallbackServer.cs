using System.Net;
using System.Net.Sockets;
using System.Text;
namespace DriveLink.Services;

public static class OAuthResultPage
{
    public static string Render(bool success, string detail)
    {
        var title = success ? "Đăng nhập thành công" : "Đăng nhập không thành công";
        var color = success ? "#4ade80" : "#f87171";
        var symbol = success ? "✓" : "!";
        return $$$"""
        <!doctype html><html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{{{title}}} · {{{AppBrand.WindowTitle}}}</title><link rel="icon" type="image/png" href="{{{AppBrand.WebIconDataUri}}}">
        <style>*{box-sizing:border-box}html{color-scheme:dark}body{margin:0;min-height:100vh;display:grid;place-items:center;padding:24px;color:#f2f5fb;font-family:"Segoe UI Variable Text","Segoe UI",Arial,sans-serif;background-color:#070a12;background-image:radial-gradient(900px 700px at 12% 8%,#3b6cff55,transparent 60%),radial-gradient(800px 700px at 92% 95%,#8b5cf650,transparent 60%),radial-gradient(600px 500px at 85% 5%,#14b8a633,transparent 60%);background-repeat:no-repeat;overflow-x:hidden}.card{position:relative;max-width:580px;width:100%;padding:36px 40px;border-radius:28px;background:linear-gradient(135deg,#ffffff1f,#ffffff08 55%,#ffffff12);border:1px solid #ffffff2b;box-shadow:inset 0 1px 0 #ffffff40,0 30px 80px #00000080;backdrop-filter:blur(24px) saturate(140%);-webkit-backdrop-filter:blur(24px) saturate(140%);animation:rise .6s cubic-bezier(.2,.9,.3,1.2) both}@keyframes rise{from{opacity:0;transform:translateY(18px) scale(.97)}to{opacity:1;transform:none}}.brand{display:flex;align-items:center;gap:20px;margin-bottom:28px}.logo{width:112px;height:112px;object-fit:contain;flex-shrink:0;filter:drop-shadow(0 10px 30px #5b8cff66)}.name{font-family:"Segoe UI Variable Display","Segoe UI",sans-serif;font-size:30px;font-weight:600}.credit{font-size:14px;color:#a6b0c4;margin-top:7px}.tagline{font-size:12px;letter-spacing:.14em;color:#717b93;margin-top:10px}.status{display:inline-flex;align-items:center;gap:10px;padding:6px 14px 6px 6px;border-radius:999px;background:{{{color}}}1f;border:1px solid {{{color}}}66;color:{{{color}}};font-size:14px;font-weight:600}.icon{display:grid;place-items:center;width:26px;height:26px;border-radius:999px;background:{{{color}}};color:#0b1020;font-size:16px;font-weight:700;box-shadow:0 0 18px {{{color}}}99}h1{font-family:"Segoe UI Variable Display","Segoe UI",sans-serif;font-size:28px;font-weight:600;line-height:1.3;margin:18px 0 10px}p{color:#a6b0c4;line-height:1.7;overflow-wrap:anywhere}.footer{margin-top:28px;padding-top:20px;border-top:1px solid #ffffff1a;font-size:13px}.version{display:block;margin-top:8px;font-size:12px;color:#717b93}@media(max-width:440px){.card{padding:24px}.logo{width:84px;height:84px}.brand{gap:14px}.name{font-size:26px}h1{font-size:23px}}@media(prefers-reduced-motion:reduce){.card{animation:none}}</style></head>
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
