using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace Win11HotspotManager.Services
{
    public class ActivityRow
    {
        public string Ip { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string UserDisplay => string.IsNullOrEmpty(Username) ? "-" : Username;
        public int Queries { get; set; }
        public DateTime LastSeen { get; set; }
        public string Mac { get; set; } = string.Empty;
        public string MacDisplay => string.IsNullOrEmpty(Mac) || Mac == "-" ? "-" : Mac;
        public long DataUsedBytes { get; set; }
        public string DataDisplay => Models.PortalUser.FormatBytes(DataUsedBytes);
        public string LastSeenDisplay
        {
            get
            {
                var sec = (DateTime.Now - LastSeen).TotalSeconds;
                if (sec < 5) return "şimdi";
                if (sec < 60) return $"{sec:F0} sn önce";
                return $"{sec / 60:F0} dk önce";
            }
        }
    }

    /// <summary>
    /// DNS aktivitesi + hotspot toplam hızı (sanal adaptör sayaçlarından).
    /// Not: istemci başına gerçek Mbps, Windows'ta sürücüsüz ölçülemediği için
    /// hız toplam olarak, cihaz/kullanıcı detayı DNS aktivitesi olarak verilir.
    /// </summary>
    public class UsageTracker
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, int> _dnsCountByIp = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastSeenByIp = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _dnsCountByUser = new(StringComparer.OrdinalIgnoreCase);

        private long _prevRx;
        private long _prevTx;
        private DateTime _prevTime = DateTime.MinValue;

        public long TotalDnsQueries { get; private set; }

        public void RecordDns(string ip, string? username)
        {
            lock (_lock)
            {
                TotalDnsQueries++;
                _dnsCountByIp[ip] = _dnsCountByIp.TryGetValue(ip, out int c) ? c + 1 : 1;
                _lastSeenByIp[ip] = DateTime.Now;
                if (!string.IsNullOrEmpty(username))
                    _dnsCountByUser[username!] = _dnsCountByUser.TryGetValue(username!, out int u) ? u + 1 : 1;
            }
        }

        public int GetUserQueryCount(string username)
        {
            lock (_lock)
            {
                return _dnsCountByUser.TryGetValue(username, out int c) ? c : 0;
            }
        }

        public List<ActivityRow> GetActivity(UserManager userManager)
        {
            lock (_lock)
            {
                var rows = new List<ActivityRow>();
                foreach (var kv in _lastSeenByIp)
                {
                    userManager.TryGetUsernameByIp(kv.Key, out string? uname);
                    rows.Add(new ActivityRow
                    {
                        Ip = kv.Key,
                        Username = uname ?? string.Empty,
                        Queries = _dnsCountByIp.TryGetValue(kv.Key, out int c) ? c : 0,
                        LastSeen = kv.Value
                    });
                }
                return rows.OrderByDescending(r => r.LastSeen).ToList();
            }
        }

        /// <summary>Hotspot arayüzünün (192.168.137.1) toplam hızı. Kapalıysa (0,0,false).</summary>
        public (double DownMbps, double UpMbps, bool Found) GetHotspotSpeed()
        {
            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                    n.OperationalStatus == OperationalStatus.Up &&
                    n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == CaptivePortalServer.PortalIp));

                if (nic == null)
                {
                    _prevTime = DateTime.MinValue;
                    return (0, 0, false);
                }

                var stats = nic.GetIPv4Statistics();
                long rx = stats.BytesReceived;
                long tx = stats.BytesSent;
                DateTime now = DateTime.Now;

                if (_prevTime == DateTime.MinValue)
                {
                    _prevRx = rx; _prevTx = tx; _prevTime = now;
                    return (0, 0, true);
                }

                double sec = Math.Max(0.2, (now - _prevTime).TotalSeconds);
                double down = Math.Max(0, (rx - _prevRx) * 8.0 / sec / 1_000_000.0);
                double up = Math.Max(0, (tx - _prevTx) * 8.0 / sec / 1_000_000.0);

                _prevRx = rx; _prevTx = tx; _prevTime = now;
                return (down, up, true);
            }
            catch
            {
                return (0, 0, false);
            }
        }
    }
}
