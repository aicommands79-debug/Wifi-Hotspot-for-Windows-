using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Win11HotspotManager.Models;

namespace Win11HotspotManager.Services
{
    /// <summary>
    /// Gerçek Captive Portal: cihaza Wi-Fi'a bağlanınca otomatik giriş ekranı açtırır.
    /// Windows / Android / iOS / Ubuntu probe isteklerini OS beklentisine uygun yanıtlar,
    /// DNS gaspı ile birleşince her HTTP isteği giriş sayfasına düşer.
    /// 80 + 8080 portlarında aynı anda dinler.
    /// </summary>
    public class CaptivePortalServer
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(uint destIp, uint srcIp, byte[] pMacAddr, ref int phyAddrLen);

        public const string PortalIp = "192.168.137.1";
        public const string PortalUrl = "http://192.168.137.1/";
        public const string PortalUrl8080 = "http://192.168.137.1:8080/";

        private const string NoCacheHeaders =
            "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
            "Pragma: no-cache\r\n" +
            "Expires: 0\r\n";

        private readonly UserManager _userManager;
        private TcpListener? _listener8080;
        private TcpListener? _listener80;
        private CancellationTokenSource? _cts;
        private bool _isRunning = false;

        public event Action<string, string, bool>? LoginAttempted;

        public bool IsRunning => _isRunning;
        public int Port => 8080;
        public bool Port80Active { get; private set; }
        public bool Port8080Active { get; private set; }
        public string LastError { get; private set; } = string.Empty;

        public CaptivePortalServer(UserManager userManager)
        {
            _userManager = userManager;
        }

        public void Start()
        {
            if (_isRunning) return;

            LastError = string.Empty;
            Port80Active = false;
            Port8080Active = false;

            _cts = new CancellationTokenSource();
            _isRunning = true;

            try
            {
                _listener8080 = new TcpListener(IPAddress.Any, 8080);
                _listener8080.Start();
                Port8080Active = true;
                _ = AcceptClientsAsync(_listener8080, _cts.Token);
            }
            catch (Exception ex)
            {
                LastError += $"8080 açılamadı: {ex.Message} ";
                System.Diagnostics.Debug.WriteLine($"Port 8080 start error: {ex.Message}");
            }

            try
            {
                _listener80 = new TcpListener(IPAddress.Any, 80);
                _listener80.Start();
                Port80Active = true;
                _ = AcceptClientsAsync(_listener80, _cts.Token);
            }
            catch (Exception ex)
            {
                LastError += $"80 açılamadı (Admin gerekli): {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Port 80 error: {ex.Message}");
            }

            // Hiçbir port açılamadıysa çalışmıyor say
            if (!Port80Active && !Port8080Active)
            {
                _isRunning = false;
                _cts?.Cancel();
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cts?.Cancel();

            try { _listener8080?.Stop(); } catch { }
            try { _listener80?.Stop(); } catch { }

            _listener8080 = null;
            _listener80 = null;
            Port80Active = false;
            Port8080Active = false;
        }

        private async Task AcceptClientsAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                try
                {
                    var tcpClient = await listener.AcceptTcpClientAsync(ct);
                    _ = HandleClientAsync(tcpClient, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    System.Diagnostics.Debug.WriteLine($"Accept client error: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    stream.ReadTimeout = 4000;
                    stream.WriteTimeout = 4000;

                    var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                    string clientIp = remoteEndPoint?.Address.ToString() ?? "127.0.0.1";
                    string clientMac = ResolveMacAddress(clientIp);

                    // --- İsteği Content-Length'e göre tam oku (gerçek cihaz POST'ları bölünebilir) ---
                    var raw = new MemoryStream();
                    byte[] buffer = new byte[8192];
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(4000);

                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
                    if (bytesRead <= 0) return;
                    raw.Write(buffer, 0, bytesRead);

                    string headerText = Encoding.ASCII.GetString(raw.ToArray());
                    int headerEnd = headerText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    int contentLength = GetContentLength(headerText);

                    // Body henüz tam gelmediyse kalanını oku
                    if (headerEnd != -1 && contentLength > 0)
                    {
                        int bodySoFar = (int)raw.Length - (headerEnd + 4);
                        int retries = 0;
                        while (bodySoFar < contentLength && retries < 10)
                        {
                            int n = await stream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
                            if (n <= 0) break;
                            raw.Write(buffer, 0, n);
                            bodySoFar += n;
                            retries++;
                        }
                    }

                    byte[] fullBytes = raw.ToArray();
                    string requestText = Encoding.UTF8.GetString(fullBytes);
                    string[] lines = requestText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    if (lines.Length == 0) return;

                    string[] requestLineParts = lines[0].Split(' ');
                    if (requestLineParts.Length < 2) return;

                    string method = requestLineParts[0].ToUpperInvariant();
                    string rawTarget = requestLineParts[1];

                    // Absolute-URI (proxy/probe) -> path'e indir: http://host/connecttest.txt -> /connecttest.txt
                    string path = NormalizePath(rawTarget);

                    string body = string.Empty;
                    int emptyLineIdx = requestText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (emptyLineIdx != -1 && emptyLineIdx + 4 < requestText.Length)
                    {
                        body = requestText.Substring(emptyLineIdx + 4);
                    }

                    bool isAuthorized = _userManager.IsAuthorized(clientIp);

                    // --- 1. OS captive-probe istekleri: beklenti dışı her yanıt = otomatik portal popup ---
                    if (IsCaptivePortalProbe(path, rawTarget))
                    {
                        await HandleProbeAsync(stream, path, isAuthorized);
                        return;
                    }

                    // --- 2. Gerçek rotalar (normalize path ile) ---
                    if (path.StartsWith("/api/login", StringComparison.OrdinalIgnoreCase) && method == "POST")
                    {
                        await HandleApiLoginAsync(stream, body, clientIp, clientMac);
                    }
                    else if (path.StartsWith("/login", StringComparison.OrdinalIgnoreCase) && method == "POST")
                    {
                        await HandleFormLoginAsync(stream, body, clientIp, clientMac);
                    }
                    else if (path.StartsWith("/api/status", StringComparison.OrdinalIgnoreCase))
                    {
                        string json = $"{{\"authorized\":{isAuthorized.ToString().ToLower()},\"ip\":\"{clientIp}\",\"mac\":\"{clientMac}\"}}";
                        await SendResponseAsync(stream, 200, "application/json", json);
                    }
                    else if (path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendResponseAsync(stream, 204, "text/plain", string.Empty);
                    }
                    else
                    {
                        // Yetkisiz her normal HTTP isteğine giriş sayfası (200) ->
                        // kullanıcı google.com yazsa bile arayüz çıkar.
                        // Yetkili ise de giriş/bilgi sayfasını göster.
                        string html = GenerateLoginPageHtml(clientIp, clientMac, isAuthorized, null, null);
                        await SendResponseAsync(stream, 200, "text/html; charset=utf-8", html);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Client handling error: {ex.Message}");
                }
            }
        }

        private static int GetContentLength(string headerText)
        {
            try
            {
                foreach (var line in headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = line.Substring("Content-Length:".Length).Trim();
                        if (int.TryParse(val, out int len) && len >= 0 && len < 1_000_000)
                            return len;
                    }
                }
            }
            catch { }
            return 0;
        }

        private static string NormalizePath(string rawTarget)
        {
            if (string.IsNullOrEmpty(rawTarget)) return "/";
            string t = rawTarget.Trim();

            // OPTIONS * gibi durumlar
            if (t == "*") return "/";

            // Absolute URI: http://host/path?query  ->  /path?query
            if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                t = t.Substring("http://".Length);
            else if (t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                t = t.Substring("https://".Length);
            else
                return StripQuery(t);

            int slash = t.IndexOf('/');
            if (slash == -1) return "/";
            return StripQuery(t.Substring(slash));
        }

        private static string StripQuery(string path)
        {
            int q = path.IndexOf('?');
            if (q != -1) path = path.Substring(0, q);
            int h = path.IndexOf('#');
            if (h != -1) path = path.Substring(0, h);
            if (string.IsNullOrEmpty(path)) return "/";
            return path;
        }

        private bool IsCaptivePortalProbe(string path, string rawTarget)
        {
            string p = (path + " " + rawTarget).ToLowerInvariant();
            return p.Contains("generate_204") ||
                   p.Contains("gen_204") ||
                   p.Contains("hotspot-detect.html") ||
                   p.Contains("connecttest.txt") ||
                   p.Contains("ncsi.txt") ||
                   p.Contains("canonical.html") ||
                   p.Contains("success.txt") ||
                   p.Contains("library/test/success.html") ||
                   p.Contains("kindle-wifi/wifistub.html") ||
                   p.Contains("/redirect") ||
                   p.Contains("connectivitycheck") ||
                   p.Contains("msftconnecttest") ||
                   p.Contains("msftncsi") ||
                   p.Contains("captive.apple.com") ||
                   p.Contains("detectportal");
        }

        private async Task HandleProbeAsync(Stream stream, string path, bool isAuthorized)
        {
            string p = path.ToLowerInvariant();

            if (!isAuthorized)
            {
                // Yetkisiz -> 302 portal'a. OS bunu görünce giriş ekranını OTOMATİK açar.
                await SendRedirectAsync(stream, PortalUrl8080);
                return;
            }

            // Yetkili -> OS'nin beklediği "başarı" yanıtı (yoksa popup kapanmaz!)
            if (p.Contains("connecttest.txt"))
            {
                await SendResponseAsync(stream, 200, "text/plain", "Microsoft Connect Test");
            }
            else if (p.Contains("ncsi.txt"))
            {
                await SendResponseAsync(stream, 200, "text/plain", "Microsoft NCSI");
            }
            else if (p.Contains("hotspot-detect.html") ||
                     p.Contains("canonical.html") ||
                     p.Contains("success.txt") ||
                     p.Contains("library/test/success.html"))
            {
                await SendResponseAsync(stream, 200, "text/html", "<HTML><HEAD><TITLE>Success</TITLE></HEAD><BODY>Success</BODY></HTML>");
            }
            else if (p.Contains("/redirect"))
            {
                await SendResponseAsync(stream, 200, "text/plain", "OK");
            }
            else
            {
                // generate_204 / gen_204 / connectivitycheck -> 204 boş
                await SendNoContentAsync(stream);
            }
        }

        private async Task HandleApiLoginAsync(Stream stream, string body, string clientIp, string clientMac)
        {
            string username, password;
            ParseFormOrJson(body, out username, out password);

            var auth = _userManager.Authenticate(username, password, clientIp, clientMac);
            LoginAttempted?.Invoke(username, clientIp, auth.Success);

            string responseJson = $"{{\"success\":{auth.Success.ToString().ToLower()},\"message\":\"{EscapeJson(auth.Message)}\"}}";
            await SendResponseAsync(stream, 200, "application/json", responseJson);
        }

        private async Task HandleFormLoginAsync(Stream stream, string body, string clientIp, string clientMac)
        {
            string username, password;
            ParseFormOrJson(body, out username, out password);

            var auth = _userManager.Authenticate(username, password, clientIp, clientMac);
            LoginAttempted?.Invoke(username, clientIp, auth.Success);

            string html = GenerateLoginPageHtml(clientIp, clientMac, auth.Success, auth.Message, username);
            await SendResponseAsync(stream, 200, "text/html; charset=utf-8", html);
        }

        private void ParseFormOrJson(string body, out string username, out string password)
        {
            username = string.Empty;
            password = string.Empty;

            if (string.IsNullOrEmpty(body)) return;

            string b = body.Trim();
            // Chunked artığı veya null byte temizliği
            b = b.Trim('\0', ' ', '\r', '\n');

            if (b.StartsWith("{"))
            {
                username = ExtractJsonValue(b, "username");
                password = ExtractJsonValue(b, "password");
            }
            else
            {
                var pairs = b.Split('&');
                foreach (var pair in pairs)
                {
                    var kv = pair.Split(new[] { '=' }, 2);
                    if (kv.Length == 2)
                    {
                        string key = WebUtility.UrlDecode(kv[0].Trim());
                        string val = WebUtility.UrlDecode(kv[1].Trim());
                        if (key.Equals("username", StringComparison.OrdinalIgnoreCase)) username = val;
                        else if (key.Equals("password", StringComparison.OrdinalIgnoreCase)) password = val;
                    }
                }
            }
        }

        private string ExtractJsonValue(string json, string key)
        {
            string target = $"\"{key}\":";
            int idx = json.IndexOf(target, StringComparison.OrdinalIgnoreCase);
            if (idx == -1) return string.Empty;

            int start = json.IndexOf('"', idx + target.Length);
            if (start == -1) return string.Empty;
            int end = json.IndexOf('"', start + 1);
            if (end == -1) return string.Empty;

            return json.Substring(start + 1, end - start - 1);
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        private async Task SendResponseAsync(Stream stream, int statusCode, string contentType, string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
            string statusText = statusCode switch
            {
                200 => "OK",
                204 => "No Content",
                302 => "Found",
                400 => "Bad Request",
                404 => "Not Found",
                _ => "OK"
            };

            string header = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                            $"Content-Type: {contentType}\r\n" +
                            $"Content-Length: {bytes.Length}\r\n" +
                            NoCacheHeaders +
                            "Access-Control-Allow-Origin: *\r\n" +
                            "Connection: close\r\n\r\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
            if (bytes.Length > 0)
            {
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }
            await stream.FlushAsync();
        }

        private async Task SendNoContentAsync(Stream stream)
        {
            string header = "HTTP/1.1 204 No Content\r\n" +
                            NoCacheHeaders +
                            "Content-Length: 0\r\n" +
                            "Connection: close\r\n\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        private async Task SendRedirectAsync(Stream stream, string location)
        {
            string header = "HTTP/1.1 302 Found\r\n" +
                            $"Location: {location}\r\n" +
                            NoCacheHeaders +
                            "Content-Length: 0\r\n" +
                            "Connection: close\r\n\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }

        private string ResolveMacAddress(string ip)
        {
            if (ip == "127.0.0.1" || ip == "::1" || ip.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return "00:00:00:00:00:00";
            }

            try
            {
                if (IPAddress.TryParse(ip, out var ipAddr))
                {
                    byte[] mac = new byte[6];
                    int len = mac.Length;
                    uint dest = BitConverter.ToUInt32(ipAddr.GetAddressBytes(), 0);
                    if (SendARP(dest, 0, mac, ref len) == 0)
                    {
                        return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                            mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
                    }
                }
            }
            catch
            {
            }
            return "-";
        }

        private string GenerateLoginPageHtml(string clientIp, string clientMac, bool isAuthorized, string? alertMessage, string? username)
        {
            string alertHtml = string.Empty;
            if (!string.IsNullOrEmpty(alertMessage))
            {
                string alertClass = isAuthorized ? "alert-success" : "alert-danger";
                string icon = isAuthorized ? "✅" : "⚠️";
                alertHtml = $"<div class='alert {alertClass}'>{icon} {WebUtility.HtmlEncode(alertMessage)}</div>";
            }

            string contentSection;
            if (isAuthorized)
            {
                contentSection = $@"
                <div class='success-box'>
                    <div class='success-icon'>🎉</div>
                    <h2>Bağlantınız Aktif!</h2>
                    <p>Hoş geldiniz <strong>{WebUtility.HtmlEncode(username ?? "Misafir")}</strong>. İnternet erişiminiz başarıyla sağlandı.</p>
                    <div class='device-info'>
                        <span>🌐 IP: <strong>{clientIp}</strong></span>
                        <span>📱 MAC: <strong>{clientMac}</strong></span>
                    </div>
                    <a href='https://www.google.com' class='btn-action'>İnterneti Kullanmaya Başla</a>
                    <p class='auto-close'>Bu pencereyi kapatabilirsiniz. Artık tüm sitelere girebilirsiniz.</p>
                </div>";
            }
            else
            {
                contentSection = $@"
                {alertHtml}
                <p class='desc'>Bu Wi-Fi ağına erişebilmek için lütfen size verilen <strong>Kişiye Özel Kullanıcı Adı ve Şifreyi</strong> giriniz.</p>
                <form method='POST' action='/login'>
                    <div class='form-group'>
                        <label for='username'>Kullanıcı Adı</label>
                        <input type='text' id='username' name='username' placeholder='Örn: misafir_1' required autofocus autocomplete='username' value='{WebUtility.HtmlEncode(username ?? "")}'>
                    </div>
                    <div class='form-group'>
                        <label for='password'>Şifre</label>
                        <input type='password' id='password' name='password' placeholder='Şifrenizi girin' required autocomplete='current-password'>
                    </div>
                    <div class='rules-notice'>
                        ℹ️ Her hesap ile yalnızca <strong>1 cihaz</strong> bağlanabilir.
                    </div>
                    <button type='submit' class='btn-submit'>Giriş Yap ve Bağlan</button>
                </form>
                <div class='device-info'>
                    <span>🌐 IP: {clientIp}</span>
                    <span>📱 MAC: {clientMac}</span>
                </div>";
            }

            return $@"<!DOCTYPE html>
<html lang='tr'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Windows 11 Wi-Fi Giriş Portalı</title>
    <style>
        * {{ box-sizing: border-box; margin: 0; padding: 0; font-family: 'Segoe UI Variable Display', 'Segoe UI', -apple-system, sans-serif; }}
        body {{
            background: #0f172a;
            color: #f8fafc;
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 20px;
        }}
        .card {{
            background: #1e293b;
            border: 1px solid #334155;
            border-radius: 16px;
            width: 100%;
            max-width: 420px;
            padding: 32px;
            box-shadow: 0 20px 25px -5px rgba(0, 0, 0, 0.5), 0 8px 10px -6px rgba(0, 0, 0, 0.5);
        }}
        .header {{ text-align: center; margin-bottom: 24px; }}
        .header .logo {{ font-size: 42px; margin-bottom: 8px; }}
        .header h1 {{ font-size: 22px; font-weight: 600; color: #ffffff; }}
        .desc {{ font-size: 13.5px; color: #94a3b8; text-align: center; margin-bottom: 24px; line-height: 1.5; }}
        .form-group {{ margin-bottom: 18px; }}
        .form-group label {{ display: block; font-size: 13px; font-weight: 500; color: #cbd5e1; margin-bottom: 6px; }}
        .form-group input {{
            width: 100%;
            padding: 12px 14px;
            border-radius: 8px;
            border: 1px solid #475569;
            background: #0f172a;
            color: #ffffff;
            font-size: 15px;
            outline: none;
            transition: border-color 0.2s;
        }}
        .form-group input:focus {{ border-color: #3b82f6; }}
        .rules-notice {{ font-size: 12px; color: #64748b; margin-bottom: 20px; text-align: center; }}
        .btn-submit {{
            width: 100%;
            padding: 13px;
            background: #2563eb;
            color: #ffffff;
            border: none;
            border-radius: 8px;
            font-size: 15px;
            font-weight: 600;
            cursor: pointer;
            transition: background 0.2s;
        }}
        .btn-submit:hover {{ background: #1d4ed8; }}
        .alert {{
            padding: 12px 14px;
            border-radius: 8px;
            font-size: 13.5px;
            margin-bottom: 20px;
            line-height: 1.4;
        }}
        .alert-danger {{ background: rgba(239, 68, 68, 0.15); border: 1px solid #ef4444; color: #fca5a5; }}
        .alert-success {{ background: rgba(34, 197, 94, 0.15); border: 1px solid #22c55e; color: #86efac; }}
        .device-info {{
            display: flex;
            justify-content: space-between;
            margin-top: 24px;
            padding-top: 16px;
            border-top: 1px solid #334155;
            font-size: 11px;
            color: #64748b;
        }}
        .success-box {{ text-align: center; padding: 12px 0; }}
        .success-icon {{ font-size: 54px; margin-bottom: 12px; }}
        .success-box h2 {{ font-size: 22px; color: #4ade80; margin-bottom: 8px; }}
        .success-box p {{ font-size: 14px; color: #cbd5e1; margin-bottom: 24px; }}
        .auto-close {{ font-size: 12px !important; color: #64748b !important; margin-top: 12px !important; margin-bottom: 0 !important; }}
        .btn-action {{
            display: inline-block;
            width: 100%;
            padding: 13px;
            background: #16a34a;
            color: #ffffff;
            text-decoration: none;
            border-radius: 8px;
            font-size: 15px;
            font-weight: 600;
            transition: background 0.2s;
        }}
        .btn-action:hover {{ background: #15803d; }}
    </style>
</head>
<body>
    <div class='card'>
        <div class='header'>
            <div class='logo'>📶</div>
            <h1>Wi-Fi Giriş Portalı</h1>
        </div>
        {contentSection}
    </div>
</body>
</html>";
        }
    }
}
