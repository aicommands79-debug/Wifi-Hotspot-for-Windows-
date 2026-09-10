using System;

namespace Win11HotspotManager.Models
{
    public class DnsLogEntry
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public string ClientIp { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public bool Allowed { get; set; }
        public bool Blocked { get; set; }

        public string TimeDisplay => Time.ToString("HH:mm:ss");
        public string UserDisplay => string.IsNullOrEmpty(Username) ? ClientIp : $"{Username} ({ClientIp})";
        public string ResultDisplay => Blocked ? "🚫 Engellendi" : (Allowed ? "✔ İletildi" : "⛔ Portala yönlendirildi");
    }
}
