using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Win11HotspotManager.Models;

namespace Win11HotspotManager.Services
{
    public class UserManager
    {
        private readonly string _storagePath;
        private readonly List<PortalUser> _users = new();
        private readonly object _lock = new();

        public event Action? UsersChanged;

        public IReadOnlyList<PortalUser> Users
        {
            get
            {
                lock (_lock)
                {
                    return _users.ToList();
                }
            }
        }

        public UserManager(string? storagePath = null)
        {
            if (string.IsNullOrEmpty(storagePath))
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dir = Path.Combine(appData, "Win11HotspotManager");
                Directory.CreateDirectory(dir);
                _storagePath = Path.Combine(dir, "users.json");
            }
            else
            {
                _storagePath = storagePath;
            }

            Load();
        }

        public void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_storagePath))
                    {
                        string json = File.ReadAllText(_storagePath);
                        var loaded = JsonSerializer.Deserialize<List<PortalUser>>(json);
                        if (loaded != null)
                        {
                            _users.Clear();
                            _users.AddRange(loaded);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"UserManager load error: {ex.Message}");
                }

                // If no users exist, create default test users
                if (_users.Count == 0)
                {
                    _users.Add(new PortalUser
                    {
                        Username = "misafir_1",
                        Password = "Password123",
                        CreatedAt = DateTime.Now
                    });
                    _users.Add(new PortalUser
                    {
                        Username = "misafir_2",
                        Password = "Password456",
                        CreatedAt = DateTime.Now
                    });
                    Save();
                }
            }
            UsersChanged?.Invoke();
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    string json = JsonSerializer.Serialize(_users, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_storagePath, json);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"UserManager save error: {ex.Message}");
                }
            }
            UsersChanged?.Invoke();
        }

        private bool _usageDirty = false;

        /// <summary>Paket sayaçlarını diske sessizce yazar (kota restart'ta korunur, UI tazelenmez).</summary>
        public void FlushUsage()
        {
            bool dirty;
            lock (_lock) { dirty = _usageDirty; _usageDirty = false; }
            if (!dirty) return;
            lock (_lock)
            {
                try
                {
                    string json = JsonSerializer.Serialize(_users, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_storagePath, json);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"UserManager flush error: {ex.Message}");
                }
            }
        }

        public PortalUser GenerateRandomUser(string prefix = "misafir")
        {
            lock (_lock)
            {
                int randomNum = RandomNumberGenerator.GetInt32(1000, 9999);
                string username = $"{prefix}_{randomNum}";
                
                // 6-digit random easy PIN/Password
                string password = RandomNumberGenerator.GetInt32(100000, 999999).ToString();

                var user = new PortalUser
                {
                    Username = username,
                    Password = password,
                    CreatedAt = DateTime.Now,
                    IsActive = true
                };

                _users.Add(user);
                Save();
                return user;
            }
        }

        public (bool Success, string Message) AddUser(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username))
                return (false, "Kullanıcı adı boş olamaz.");

            if (string.IsNullOrWhiteSpace(password))
                return (false, "Şifre boş olamaz.");

            username = username.Trim();

            lock (_lock)
            {
                if (_users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                {
                    return (false, "Bu kullanıcı adı zaten mevcut.");
                }

                _users.Add(new PortalUser
                {
                    Username = username,
                    Password = password.Trim(),
                    CreatedAt = DateTime.Now,
                    IsActive = true
                });

                Save();
                return (true, "Kullanıcı başarıyla oluşturuldu.");
            }
        }

        public (bool Success, string Message) RemoveUser(string username)
        {
            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                    return (false, "Kullanıcı bulunamadı.");

                _users.Remove(user);
                Save();
                return (true, "Kullanıcı silindi.");
            }
        }

        public (bool Success, string Message) ResetBinding(string username)
        {
            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                    return (false, "Kullanıcı bulunamadı.");

                AccumulateSessionLocked(user);
                user.BoundMacAddress = null;
                user.BoundIpAddress = null;
                user.IsLoggedIn = false;
                Save();
                return (true, "Kullanıcı bağlantısı sıfırlandı ve boşa çıkarıldı.");
            }
        }

        /// <summary>Oturumu kapatır ve geçen süreyi kullanıma işler (kota takibi için).</summary>
        public (bool Success, string Message) Logout(string username)
        {
            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                    return (false, "Kullanıcı bulunamadı.");

                AccumulateSessionLocked(user);
                user.IsLoggedIn = false;
                Save();
                return (true, "Oturum kapatıldı.");
            }
        }

        public (bool Success, string Message) SetTimeQuota(string username, int minutes)
        {
            if (minutes < 0 || minutes > 10080)
                return (false, "Kota 0-10080 dakika arası olmalıdır (0 = sınırsız).");

            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                    return (false, "Kullanıcı bulunamadı.");

                user.TimeQuotaMinutes = minutes;
                Save();
                return (true, minutes == 0 ? "Kota kaldırıldı (sınırsız)." : $"Süre kotası {minutes} dk olarak ayarlandı.");
            }
        }

        public (bool Success, string Message) ResetUsage(string username)
        {
            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                    return (false, "Kullanıcı bulunamadı.");

                user.UsedMinutes = 0;
                user.UsedBytes = 0;
                // Aktif oturum varsa sayaçları sıfırdan başlat
                if (user.IsLoggedIn)
                    user.LastLoginTime = DateTime.Now;
                Save();
                return (true, "Kullanım sayaçları (süre + veri) sıfırlandı.");
            }
        }

        public (bool Success, string Message) SetSpeedLimit(string username, double mbps)
        {
            if (mbps < 0 || mbps > 1000)
                return (false, "Hız limiti 0-1000 Mbps arası olmalıdır (0 = sınırsız).");

            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                    return (false, "Kullanıcı bulunamadı.");

                user.SpeedLimitMbps = mbps;
                Save();
                return (true, mbps == 0 ? "Hız limiti kaldırıldı." : $"Hız limiti {mbps} Mbps olarak ayarlandı.");
            }
        }

        public (bool Success, string Message) SetDataQuota(string username, long bytes)
        {
            if (bytes < 0 || bytes > 1_000_000_000_000L)
                return (false, "Veri kotası 0-1000 GB arası olmalıdır (0 = sınırsız).");

            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                    return (false, "Kullanıcı bulunamadı.");

                user.DataQuotaBytes = bytes;
                Save();
                return (true, bytes == 0 ? "Veri kotası kaldırıldı." : $"Veri kotası {PortalUser.FormatBytes(bytes)} olarak ayarlandı.");
            }
        }

        /// <summary>Paket boyutunu kullanıcının sayacına işler (IP üzerinden eşleşir).</summary>
        public void AddUsageByIp(string ip, long bytes)
        {
            if (string.IsNullOrEmpty(ip) || bytes <= 0) return;
            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.BoundIpAddress, ip, StringComparison.OrdinalIgnoreCase));
                if (user != null)
                {
                    user.UsedBytes += bytes;
                    _usageDirty = true;
                }
            }
        }

        /// <summary>Trafik sürücüsü için anlık politika: (kullanıcıAdı, hızMbps, veriKota, veriKullanım).</summary>
        public (string? Username, double Mbps, long QuotaBytes, long UsedBytes) GetClientPolicy(string ip)
        {
            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.BoundIpAddress, ip, StringComparison.OrdinalIgnoreCase));
                if (user == null) return (null, 0, 0, 0);
                return (user.Username, user.SpeedLimitMbps, user.DataQuotaBytes, user.UsedBytes);
            }
        }

        /// <summary>Veri kotası dolmuş + girişli kullanıcılar (periyodik kontrol için).</summary>
        public List<string> GetDataQuotaExpired()
        {
            lock (_lock)
            {
                return _users
                    .Where(u => u.IsLoggedIn && u.DataQuotaBytes > 0 && u.UsedBytes >= u.DataQuotaBytes)
                    .Select(u => u.Username)
                    .ToList();
            }
        }

        public bool TryGetUsernameByIp(string ip, out string? username)
        {
            username = null;
            if (string.IsNullOrEmpty(ip)) return false;

            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.BoundIpAddress, ip, StringComparison.OrdinalIgnoreCase));
                if (user == null) return false;
                username = user.Username;
                return true;
            }
        }

        private static void AccumulateSessionLocked(PortalUser user)
        {
            if (user.IsLoggedIn && user.LastLoginTime.HasValue)
            {
                user.UsedMinutes += Math.Max(0, (DateTime.Now - user.LastLoginTime.Value).TotalMinutes);
            }
        }

        public (bool Success, string Message, PortalUser? User) Authenticate(string username, string password, string clientIp, string clientMac)
        {
            lock (_lock)
            {
                var user = _users.FirstOrDefault(u => string.Equals(u.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));
                if (user == null || user.Password != password.Trim())
                {
                    return (false, "Hatalı kullanıcı adı veya şifre.", null);
                }

                if (!user.IsActive)
                {
                    return (false, "Bu kullanıcı hesabı devre dışı bırakılmıştır.", null);
                }

                // SÜRE KOTASI KONTROLÜ
                if (user.TimeQuotaMinutes > 0 && user.UsedMinutes >= user.TimeQuotaMinutes)
                {
                    return (false, $"Süre kotanız doldu ({user.TimeQuotaMinutes} dk). Yöneticiden kota isteyin.", null);
                }

                // VERİ KOTASI KONTROLÜ
                if (user.DataQuotaBytes > 0 && user.UsedBytes >= user.DataQuotaBytes)
                {
                    return (false, $"Veri kotanız doldu ({PortalUser.FormatBytes(user.DataQuotaBytes)}). Yöneticiden kota isteyin.", null);
                }

                // TEK KİŞİ / TEK CİHAZ KONTROLÜ
                // Eğer hesap zaten bir cihaza kilitliyse ve gelen cihaz farklı bir MAC/IP ise engelle
                if (!string.IsNullOrEmpty(user.BoundMacAddress) && 
                    !string.Equals(user.BoundMacAddress, clientMac, StringComparison.OrdinalIgnoreCase) &&
                    clientMac != "00:00:00:00:00:00" && clientMac != "-")
                {
                    return (false, $"Bu hesap şu anda başka bir cihazda ({user.BoundMacAddress}) kullanımda! Aynı hesapla yalnızca 1 kişi bağlanabilir.", null);
                }

                // Eğer MAC henüz bilinmiyorsa ama başka bir IP'den oturum açıksa
                if (user.IsLoggedIn && 
                    !string.IsNullOrEmpty(user.BoundIpAddress) && 
                    !string.Equals(user.BoundIpAddress, clientIp, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, $"Bu hesap şu anda başka bir IP adresinde ({user.BoundIpAddress}) aktif! Aynı hesapla yalnızca 1 kişi bağlanabilir.", null);
                }

                // Başarılı giriş: Hesabı bu cihaza bağla
                if (clientMac != "00:00:00:00:00:00" && clientMac != "-")
                {
                    user.BoundMacAddress = clientMac;
                }
                user.BoundIpAddress = clientIp;
                user.IsLoggedIn = true;
                user.LastLoginTime = DateTime.Now;

                Save();
                return (true, $"Hoş geldiniz {user.Username}! İnternet erişiminiz başarıyla sağlandı.", user);
            }
        }

        public bool IsAuthorized(string clientIp)
        {
            if (string.IsNullOrEmpty(clientIp)) return false;

            // Host/Gateway ve loopback her zaman yetkilidir
            if (clientIp == "127.0.0.1" || clientIp == "::1" || clientIp == "192.168.137.1")
            {
                return true;
            }

            lock (_lock)
            {
                return _users.Any(u => u.IsLoggedIn && string.Equals(u.BoundIpAddress, clientIp, StringComparison.OrdinalIgnoreCase));
            }
        }

        public HashSet<string> GetAuthorizedIps()
        {
            lock (_lock)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var u in _users)
                {
                    if (u.IsLoggedIn && !string.IsNullOrEmpty(u.BoundIpAddress))
                    {
                        set.Add(u.BoundIpAddress);
                    }
                }
                return set;
            }
        }
    }
}
