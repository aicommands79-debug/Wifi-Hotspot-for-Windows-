using System;

namespace Win11HotspotManager.Models
{
    public class PortalUser
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public string? BoundMacAddress { get; set; }
        public string? BoundIpAddress { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? LastLoginTime { get; set; }
        public bool IsLoggedIn { get; set; } = false;

        /// <summary>Süre kotası (dakika). 0 = sınırsız.</summary>
        public int TimeQuotaMinutes { get; set; } = 0;

        /// <summary>Bugüne kadar birikmiş kullanım (dakika).</summary>
        public double UsedMinutes { get; set; } = 0;

        /// <summary>Hız limiti (Mbps, her yön için). 0 = sınırsız. Sürücü aktifken uygulanır.</summary>
        public double SpeedLimitMbps { get; set; } = 0;

        /// <summary>Veri kotası (bayt, toplam up+down). 0 = sınırsız. Sürücü aktifken ölçülür.</summary>
        public long DataQuotaBytes { get; set; } = 0;

        /// <summary>Birikmiş veri kullanımı (bayt, toplam up+down).</summary>
        public long UsedBytes { get; set; } = 0;

        public double LiveUsedMinutes
        {
            get
            {
                double live = UsedMinutes;
                if (IsLoggedIn && LastLoginTime.HasValue)
                    live += (DateTime.Now - LastLoginTime.Value).TotalMinutes;
                return live;
            }
        }

        public string QuotaDisplay
        {
            get
            {
                if (TimeQuotaMinutes <= 0) return "∞ Sınırsız";
                double remaining = TimeQuotaMinutes - LiveUsedMinutes;
                if (remaining <= 0) return "⏱ Doldu";
                return $"{remaining:F0} dk";
            }
        }

        public string SessionDisplay
        {
            get
            {
                if (IsLoggedIn && LastLoginTime.HasValue)
                    return $"{(DateTime.Now - LastLoginTime.Value).TotalMinutes:F0} dk";
                return "-";
            }
        }

        public string SpeedDisplay => SpeedLimitMbps > 0 ? $"{SpeedLimitMbps:F0} Mbps" : "∞";

        public string DataDisplay
        {
            get
            {
                if (DataQuotaBytes <= 0) return $"{FormatBytes(UsedBytes)} / ∞";
                if (UsedBytes >= DataQuotaBytes) return "⛔ Doldu";
                return $"{FormatBytes(UsedBytes)} / {FormatBytes(DataQuotaBytes)}";
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1_000_000_000) return $"{bytes / 1_000_000_000.0:F1} GB";
            if (bytes >= 1_000_000) return $"{bytes / 1_000_000.0:F0} MB";
            if (bytes >= 1_000) return $"{bytes / 1_000.0:F0} KB";
            return $"{bytes} B";
        }

        public string StatusDisplay
        {
            get
            {
                if (!IsActive) return "Devre Dışı";
                if (IsLoggedIn) return "Çevrimiçi (Bağlı)";
                if (!string.IsNullOrEmpty(BoundMacAddress)) return "Cihaza Kilitli";
                return "Kullanıma Hazır";
            }
        }

        public string DeviceDisplay => !string.IsNullOrEmpty(BoundIpAddress) 
            ? $"{BoundIpAddress} ({BoundMacAddress ?? "MAC Yok"})" 
            : (BoundMacAddress ?? "-");
    }
}
