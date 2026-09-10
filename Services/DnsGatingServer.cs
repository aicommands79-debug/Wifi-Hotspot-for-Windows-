using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Win11HotspotManager.Services
{
    /// <summary>
    /// Gerçek DNS gaspı: giriş yapmamış cihazın tüm A sorgularını 192.168.137.1'e
    /// yönlendirir -> telefonda/PC'de giriş ekranı OTOMATİK açılır.
    /// Giriş yapmış cihazlar upstream DNS'e (8.8.8.8) aynen iletilir.
    /// AAAA (IPv6) sorgularına boş yanıt verilir ki cihaz IPv6 üzerinden kaçamasın.
    /// </summary>
    public class DnsGatingServer
    {
        private readonly UserManager _userManager;
        private UdpClient? _udpListener;
        private CancellationTokenSource? _cts;
        private bool _isRunning = false;
        private readonly IPAddress _portalIp = IPAddress.Parse(CaptivePortalServer.PortalIp);
        private readonly IPEndPoint _upstreamDns = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53);
        private readonly IPEndPoint _upstreamDnsBackup = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53);

        public bool IsRunning => _isRunning;
        public string LastError { get; private set; } = string.Empty;

        /// <summary>Her DNS sorgusunda tetiklenir: (clientIp, domain, qtype, iletildiMi, engellendiMi)</summary>
        public event Action<string, string, ushort, bool, bool>? DnsQueried;

        public Func<string, bool>? BlocklistChecker;

        public DnsGatingServer(UserManager userManager)
        {
            _userManager = userManager;
        }

        public bool Start()
        {
            if (_isRunning) return true;
            LastError = string.Empty;

            try
            {
                _cts = new CancellationTokenSource();
                _udpListener = new UdpClient(new IPEndPoint(IPAddress.Any, 53));
                _isRunning = true;
                _ = ListenAsync(_cts.Token);
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"DNS 53 açılamadı (Admin gerekli): {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"DNS Server start error: {ex.Message}");
                _isRunning = false;
                return false;
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cts?.Cancel();

            try { _udpListener?.Close(); } catch { }
            _udpListener = null;
        }

        private async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning && _udpListener != null)
            {
                try
                {
                    var result = await _udpListener.ReceiveAsync(ct);
                    _ = ProcessDnsQueryAsync(result.Buffer, result.RemoteEndPoint, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    System.Diagnostics.Debug.WriteLine($"DNS Receive error: {ex.Message}");
                }
            }
        }

        private async Task ProcessDnsQueryAsync(byte[] queryBuffer, IPEndPoint clientEndPoint, CancellationToken ct)
        {
            if (queryBuffer.Length < 12) return;

            string clientIp = clientEndPoint.Address.ToString();
            string domain = ParseDomainName(queryBuffer);
            ushort qtype = GetQueryType(queryBuffer);

            // Gateway / host her zaman serbest
            if (clientIp == "127.0.0.1" || clientIp == "::1" || clientIp == CaptivePortalServer.PortalIp)
            {
                byte[]? fwd = await ForwardToUpstreamAsync(queryBuffer);
                if (fwd != null && _udpListener != null)
                {
                    try { await _udpListener.SendAsync(fwd, fwd.Length, clientEndPoint); } catch { }
                }
                try { DnsQueried?.Invoke(clientIp, domain, qtype, true, false); } catch { }
                return;
            }

            bool isAuthorized = _userManager.IsAuthorized(clientIp);

            if (isAuthorized)
            {
                // Site engelleme: girişli cihazda bile kara listedekiler çözülmez
                bool blocked = false;
                try { blocked = BlocklistChecker?.Invoke(domain) == true; } catch { }
                if (blocked)
                {
                    byte[] nodata = CraftNodataResponse(queryBuffer);
                    if (_udpListener != null)
                    {
                        try { await _udpListener.SendAsync(nodata, nodata.Length, clientEndPoint); } catch { }
                    }
                    try { DnsQueried?.Invoke(clientIp, domain, qtype, false, true); } catch { }
                    return;
                }

                byte[]? response = await ForwardToUpstreamAsync(queryBuffer);
                if (response != null && _udpListener != null)
                {
                    try { await _udpListener.SendAsync(response, response.Length, clientEndPoint); } catch { }
                }
                try { DnsQueried?.Invoke(clientIp, domain, qtype, response != null, false); } catch { }
                return;
            }

            // Yetkisiz cihaz:
            byte[] answer;
            bool treatedAsAllowed = false;
            if (qtype == 28) // AAAA -> boş (IPv6 kaçışını kapat)
            {
                answer = CraftNodataResponse(queryBuffer);
            }
            else if (qtype == 1) // A -> portala gasp et
            {
                answer = CraftSpoofedResponse(queryBuffer, _portalIp);
            }
            else
            {
                // MX/TXT/SRV vb: upstream'e ilet (HTTP yine de portala düşer çünkü A gasplı)
                byte[]? fwd = await ForwardToUpstreamAsync(queryBuffer);
                answer = fwd ?? CraftNodataResponse(queryBuffer);
                treatedAsAllowed = fwd != null;
            }

            if (_udpListener != null)
            {
                try { await _udpListener.SendAsync(answer, answer.Length, clientEndPoint); } catch { }
            }
            try { DnsQueried?.Invoke(clientIp, domain, qtype, treatedAsAllowed, false); } catch { }
        }

        private static ushort GetQueryType(byte[] query)
        {
            try
            {
                int pos = 12;
                while (pos < query.Length && query[pos] != 0)
                {
                    int len = query[pos];
                    // Sıkıştırma işaretçisi varsa (0xC0) dur
                    if ((len & 0xC0) == 0xC0) { pos += 2; break; }
                    pos += 1 + len;
                    if (pos + 4 > query.Length + 16) break;
                }
                if (pos < query.Length && query[pos] == 0) pos++;
                if (pos + 2 <= query.Length)
                {
                    return (ushort)((query[pos] << 8) | query[pos + 1]);
                }
            }
            catch { }
            return 1;
        }

        /// <summary>Sorgudaki alan adını (QNAME) okur, örn. "www.google.com".</summary>
        public static string ParseDomainName(byte[] query)
        {
            try
            {
                var labels = new System.Collections.Generic.List<string>();
                int pos = 12;
                int jumps = 0;
                while (pos < query.Length && query[pos] != 0 && jumps < 10)
                {
                    int len = query[pos];
                    if ((len & 0xC0) == 0xC0)
                    {
                        // Sıkıştırma işaretçisi: istemci sorgularında nadir, takip et
                        if (pos + 1 >= query.Length) break;
                        pos = ((len & 0x3F) << 8) | query[pos + 1];
                        jumps++;
                        continue;
                    }
                    if (len > 63 || pos + 1 + len > query.Length) break;
                    labels.Add(System.Text.Encoding.ASCII.GetString(query, pos + 1, len));
                    pos += 1 + len;
                }
                if (labels.Count == 0) return "(boş)";
                return string.Join(".", labels);
            }
            catch
            {
                return "(okunamadı)";
            }
        }

        private async Task<byte[]?> ForwardToUpstreamAsync(byte[] query)
        {
            try
            {
                using var upstream = new UdpClient();
                upstream.Client.ReceiveTimeout = 2000;
                upstream.Client.SendTimeout = 2000;

                await upstream.SendAsync(query, query.Length, _upstreamDns);

                using var cts = new CancellationTokenSource(2000);
                var result = await upstream.ReceiveAsync(cts.Token).AsTask();
                return result.Buffer;
            }
            catch
            {
                try
                {
                    using var backup = new UdpClient();
                    backup.Client.ReceiveTimeout = 2000;
                    backup.Client.SendTimeout = 2000;

                    await backup.SendAsync(query, query.Length, _upstreamDnsBackup);
                    using var cts2 = new CancellationTokenSource(2000);
                    var result2 = await backup.ReceiveAsync(cts2.Token).AsTask();
                    return result2.Buffer;
                }
                catch
                {
                    return null;
                }
            }
        }

        private static int GetQuestionSectionLength(byte[] query)
        {
            int pos = 12;
            while (pos < query.Length && query[pos] != 0)
            {
                int len = query[pos];
                if ((len & 0xC0) == 0xC0) { pos += 2; goto done; }
                pos += 1 + len;
            }
            pos++; // terminating zero
            pos += 4; // QTYPE + QCLASS
        done:
            return Math.Min(pos, query.Length) - 12;
        }

        private static byte[] CraftSpoofedResponse(byte[] query, IPAddress spoofIp)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(query[0]);
            writer.Write(query[1]);
            writer.Write((byte)0x81);
            writer.Write((byte)0x80);
            writer.Write((byte)0x00);
            writer.Write((byte)0x01);
            writer.Write((byte)0x00);
            writer.Write((byte)0x01);
            writer.Write((byte)0x00);
            writer.Write((byte)0x00);
            writer.Write((byte)0x00);
            writer.Write((byte)0x00);

            int qLen = GetQuestionSectionLength(query);
            if (qLen > 0) writer.Write(query, 12, qLen);

            writer.Write((byte)0xC0);
            writer.Write((byte)0x0C);
            writer.Write((byte)0x00);
            writer.Write((byte)0x01);
            writer.Write((byte)0x00);
            writer.Write((byte)0x01);
            writer.Write((byte)0x00);
            writer.Write((byte)0x00);
            writer.Write((byte)0x00);
            writer.Write((byte)0x3C); // TTL 60
            writer.Write((byte)0x00);
            writer.Write((byte)0x04);
            writer.Write(spoofIp.GetAddressBytes());

            return ms.ToArray();
        }

        private static byte[] CraftNodataResponse(byte[] query)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(query[0]);
            writer.Write(query[1]);
            writer.Write((byte)0x81);
            writer.Write((byte)0x80);
            writer.Write((byte)0x00);
            writer.Write((byte)0x01);
            writer.Write((byte)0x00);
            writer.Write((byte)0x00); // ANCOUNT 0
            writer.Write((byte)0x00);
            writer.Write((byte)0x00);
            writer.Write((byte)0x00);
            writer.Write((byte)0x00);

            int qLen = GetQuestionSectionLength(query);
            if (qLen > 0) writer.Write(query, 12, qLen);

            return ms.ToArray();
        }
    }
}
