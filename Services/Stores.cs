using System;
using System.IO;
using System.Text.Json;

namespace Win11HotspotManager.Services
{
    /// <summary>settings.json: giriş modu + zamanlayıcı. Alan eklerken Load/Save uyumlu kalır.</summary>
    public static class AppSettings
    {
        private static string SettingsPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win11HotspotManager");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "settings.json");
            }
        }

        public static string AuthMode { get; set; } = "Portal";
        public static bool SchedEnabled { get; set; } = false;
        public static string SchedStart { get; set; } = "08:00";
        public static string SchedStop { get; set; } = "23:00";

        private class SettingsDto
        {
            public string AuthMode { get; set; } = "Portal";
            public bool SchedEnabled { get; set; } = false;
            public string SchedStart { get; set; } = "08:00";
            public string SchedStop { get; set; } = "23:00";
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(SettingsPath));
                if (dto == null) return;
                AuthMode = string.IsNullOrEmpty(dto.AuthMode) ? "Portal" : dto.AuthMode;
                SchedEnabled = dto.SchedEnabled;
                SchedStart = string.IsNullOrEmpty(dto.SchedStart) ? "08:00" : dto.SchedStart;
                SchedStop = string.IsNullOrEmpty(dto.SchedStop) ? "23:00" : dto.SchedStop;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AppSettings load error: {ex.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                var dto = new SettingsDto
                {
                    AuthMode = AuthMode,
                    SchedEnabled = SchedEnabled,
                    SchedStart = SchedStart,
                    SchedStop = SchedStop
                };
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AppSettings save error: {ex.Message}");
            }
        }
    }

    public class PortalSettings
    {
        public string BusinessName { get; set; } = string.Empty;
        public string Announcement { get; set; } = string.Empty;
    }

    public class PortalSettingsStore
    {
        private readonly string _path;
        private readonly object _lock = new();
        private PortalSettings _current = new();

        public PortalSettingsStore()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win11HotspotManager");
            try { Directory.CreateDirectory(dir); } catch { }
            _path = Path.Combine(dir, "portalsettings.json");
            Load();
        }

        public void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_path))
                    {
                        var loaded = JsonSerializer.Deserialize<PortalSettings>(File.ReadAllText(_path));
                        if (loaded != null) _current = loaded;
                    }
                }
                catch { }
            }
        }

        public PortalSettings Get()
        {
            lock (_lock)
            {
                return new PortalSettings { BusinessName = _current.BusinessName, Announcement = _current.Announcement };
            }
        }

        public void Update(string businessName, string announcement)
        {
            lock (_lock)
            {
                _current = new PortalSettings
                {
                    BusinessName = (businessName ?? string.Empty).Trim(),
                    Announcement = (announcement ?? string.Empty).Trim()
                };
                try { File.WriteAllText(_path, JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true })); } catch { }
            }
        }
    }

    public class BlocklistStore
    {
        private readonly string _path;
        private readonly object _lock = new();
        private List<string> _domains = new();

        public BlocklistStore()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win11HotspotManager");
            try { Directory.CreateDirectory(dir); } catch { }
            _path = Path.Combine(dir, "blocklist.json");
            Load();
        }

        public void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_path))
                    {
                        var loaded = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path));
                        if (loaded != null) _domains = loaded.Select(Normalize).Where(d => d.Length > 0).Distinct().ToList();
                    }
                }
                catch { }
            }
        }

        public List<string> GetAll()
        {
            lock (_lock) { return _domains.ToList(); }
        }

        public bool Add(string domain)
        {
            string d = Normalize(domain);
            if (d.Length < 3 || !d.Contains('.')) return false;
            lock (_lock)
            {
                if (_domains.Contains(d)) return false;
                _domains.Add(d);
                SaveLocked();
                return true;
            }
        }

        public bool Remove(string domain)
        {
            string d = Normalize(domain);
            lock (_lock)
            {
                bool removed = _domains.Remove(d);
                if (removed) SaveLocked();
                return removed;
            }
        }

        public bool IsBlocked(string domain)
        {
            string d = Normalize(domain);
            if (d.Length == 0) return false;
            lock (_lock)
            {
                foreach (var entry in _domains)
                {
                    if (d.Equals(entry, StringComparison.OrdinalIgnoreCase) ||
                        d.EndsWith("." + entry, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
        }

        private static string Normalize(string? domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return string.Empty;
            return domain.Trim().TrimEnd('.').ToLowerInvariant();
        }

        private void SaveLocked()
        {
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_domains, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        }
    }
}
