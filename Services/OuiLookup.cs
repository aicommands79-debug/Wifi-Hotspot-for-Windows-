using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Win11HotspotManager.Services
{
    /// <summary>
    /// MAC öneki → üretici. IEEE listesinden beslenir (30 gün önbellekli),
    /// çevrimdışıysa gömülü mini liste. Rastgele MAC'leri ayrıca işaretler.
    /// </summary>
    public static class OuiLookup
    {
        private static readonly object _lock = new();
        private static Dictionary<string, string> _table = new(StringComparer.OrdinalIgnoreCase)
        {
            { "00:50:56", "VMware" },
            { "00:0C:29", "VMware" },
            { "00:05:69", "VMware" },
            { "08:00:27", "VirtualBox" },
            { "52:54:00", "QEMU/KVM" },
            { "00:15:5D", "Hyper-V" },
        };
        private static bool _loadStarted = false;

        public static string Lookup(string? mac)
        {
            string? prefix = GetPrefix(mac);
            if (prefix == null) return "-";
            if (IsRandomized(prefix)) return "Rastgele MAC";
            EnsureLoaded();
            lock (_lock)
            {
                return _table.TryGetValue(prefix, out string? v) && !string.IsNullOrEmpty(v) ? v : "Bilinmiyor";
            }
        }

        public static void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_loadStarted) return;
                _loadStarted = true;
            }
            _ = Task.Run(LoadAsync);
        }

        private static string? GetPrefix(string? mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return null;
            string clean = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            if (clean.Length < 6) return null;
            return $"{clean[0]}{clean[1]}:{clean[2]}{clean[3]}:{clean[4]}{clean[5]}";
        }

        private static bool IsRandomized(string prefix)
        {
            // U/L biti: ikinci hex hanenin 2'ler basamağı kuruluysa MAC rastgeledir
            char c = prefix[1];
            int val = c >= '0' && c <= '9' ? c - '0' : c - 'A' + 10;
            return val >= 0 && val <= 15 && (val & 2) != 0;
        }

        private static async Task LoadAsync()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win11HotspotManager");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "oui.txt");

                bool fresh = File.Exists(path) && (DateTime.Now - File.GetLastWriteTime(path)).TotalDays < 30;
                if (!fresh)
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    string text = await http.GetStringAsync("https://standards-oui.ieee.org/oui/oui.txt");
                    if (!string.IsNullOrEmpty(text) && text.Contains("(hex)"))
                    {
                        await File.WriteAllTextAsync(path, text);
                    }
                    else if (!File.Exists(path))
                    {
                        return;
                    }
                }

                var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadLines(path))
                {
                    int hex = line.IndexOf("(hex)", StringComparison.OrdinalIgnoreCase);
                    if (hex < 0) continue;
                    string rawPrefix = line.Substring(0, hex).Trim().Replace('-', ':').ToUpperInvariant();
                    if (rawPrefix.Length != 8) continue;
                    string vendor = line.Substring(hex + 5).Trim();
                    if (vendor.Length == 0 || parsed.ContainsKey(rawPrefix)) continue;
                    parsed[rawPrefix] = vendor;
                }

                if (parsed.Count > 1000)
                {
                    lock (_lock)
                    {
                        foreach (var kv in parsed) _table[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }
        }
    }
}
