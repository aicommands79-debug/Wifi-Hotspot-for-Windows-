using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Win11HotspotManager.Services
{
    public class DeviceLimit
    {
        public double Mbps { get; set; } = 0;
        public long Bytes { get; set; } = 0;
    }

    /// <summary>
    /// MAC adresi bazında cihaz limitleri (devicelimits.json içinde saklanır).
    /// </summary>
    public class DeviceLimitStore
    {
        private readonly string _path;
        private readonly object _lock = new();
        private Dictionary<string, DeviceLimit> _limits = new(StringComparer.OrdinalIgnoreCase);

        public DeviceLimitStore()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win11HotspotManager");
            try { Directory.CreateDirectory(dir); } catch { }
            _path = Path.Combine(dir, "devicelimits.json");
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
                        var loaded = JsonSerializer.Deserialize<Dictionary<string, DeviceLimit>>(File.ReadAllText(_path));
                        if (loaded != null) _limits = new Dictionary<string, DeviceLimit>(loaded, StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch { }
            }
        }

        private void SaveLocked()
        {
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(_limits, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public DeviceLimit? Get(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return null;
            lock (_lock)
            {
                return _limits.TryGetValue(mac.Trim(), out var l) ? l : null;
            }
        }

        public void Set(string mac, double mbps, long bytes)
        {
            lock (_lock)
            {
                _limits[mac.Trim()] = new DeviceLimit { Mbps = mbps, Bytes = bytes };
                SaveLocked();
            }
        }

        public void Clear(string mac)
        {
            lock (_lock)
            {
                if (_limits.Remove(mac.Trim())) SaveLocked();
            }
        }
    }
}
