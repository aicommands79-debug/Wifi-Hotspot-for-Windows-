using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win11HotspotManager.Services
{
    /// <summary>
    /// WinDivert (NETWORK_FORWARD) ile hotspot istemcilerinin paketlerini görür:
    /// bayt sayar (veri kotası), token-bucket ile hız policingi yapar, kota dolanı düşürür.
    /// Paketler değiştirilmeden geri enjekte edilir; sürücü yoksa/hata olursa
    /// sessizce pasif kalır, uygulamanın geri kalanı çalışmaya devam eder.
    /// Hız limiti her yön için ayrı uygulanır (down ≤ X ve up ≤ X).
    /// </summary>
    public class TrafficMeter
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(uint destIp, uint srcIp, byte[] pMacAddr, ref int phyAddrLen);

        private const int AddrSize = 128;
        private const int PacketBufSize = 65535;

        private readonly UserManager _userManager;
        private readonly Func<string, DeviceLimit?> _deviceLimitForMac;

        private IntPtr _handle = IntPtr.Zero;
        private volatile bool _running = false;
        private Thread? _thread;
        private readonly object _lock = new();
        private readonly Dictionary<string, TokenBucket> _buckets = new();
        private readonly Dictionary<string, string> _macCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _deviceBytes = new(StringComparer.OrdinalIgnoreCase);

        public bool IsAvailable { get; }
        public bool IsRunning { get; private set; }
        public string Status { get; private set; } = "Başlatılmadı";
        public long TotalSeen;
        public long TotalDropped;

        public TrafficMeter(UserManager userManager, Func<string, DeviceLimit?> deviceLimitForMac)
        {
            _userManager = userManager;
            _deviceLimitForMac = deviceLimitForMac;
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                IsAvailable = File.Exists(Path.Combine(dir, "WinDivert.dll")) &&
                              File.Exists(Path.Combine(dir, "WinDivert64.sys"));
                if (!IsAvailable) Status = "Sürücü dosyası yok (WinDivert.dll)";
            }
            catch
            {
                IsAvailable = false;
                Status = "Sürücü kontrolü başarısız";
            }
        }

        public bool Start()
        {
            if (IsRunning) return true;
            if (!IsAvailable)
            {
                Status = "Sürücü dosyası yok — hız/kota (GB) çalışmaz";
                return false;
            }

            try
            {
                IntPtr h = WinDivertNative.WinDivertOpen("ip", WinDivertNative.LAYER_NETWORK_FORWARD, 0, 0);
                if (h == IntPtr.Zero || h == WinDivertNative.INVALID_HANDLE)
                {
                    int err = Marshal.GetLastWin32Error();
                    Status = $"Sürücü açılamadı (hata {err}). Admin + Secure Boot uyumu gerekli.";
                    IsRunning = false;
                    return false;
                }

                try
                {
                    WinDivertNative.WinDivertSetParam(h, WinDivertNative.PARAM_QUEUE_LENGTH, 4096);
                    WinDivertNative.WinDivertSetParam(h, WinDivertNative.PARAM_QUEUE_TIME, 2000);
                }
                catch { }

                _handle = h;
                _running = true;
                IsRunning = true;
                Status = "✔ Aktif — ölçüm + limit uygulanıyor";
                _thread = new Thread(Loop) { IsBackground = true, Name = "TrafficMeter" };
                _thread.Start();
                return true;
            }
            catch (DllNotFoundException)
            {
                Status = "WinDivert.dll yüklenemedi";
                IsRunning = false;
                return false;
            }
            catch (Exception ex)
            {
                Status = $"Başlatma hatası: {ex.Message}";
                IsRunning = false;
                return false;
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            _running = false;
            try
            {
                if (_handle != IntPtr.Zero && _handle != WinDivertNative.INVALID_HANDLE)
                    WinDivertNative.WinDivertShutdown(_handle, WinDivertNative.SHUTDOWN_BOTH);
            }
            catch { }
            try { _thread?.Join(2500); } catch { }
            try
            {
                if (_handle != IntPtr.Zero && _handle != WinDivertNative.INVALID_HANDLE)
                    WinDivertNative.WinDivertClose(_handle);
            }
            catch { }
            _handle = IntPtr.Zero;
            _thread = null;
            Status = "Durduruldu";
        }

        public void ResetShapers()
        {
            lock (_lock) { _buckets.Clear(); }
        }

        public long GetDeviceBytes(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return 0;
            lock (_lock)
            {
                return _deviceBytes.TryGetValue(mac.Trim(), out long b) ? b : 0;
            }
        }

        public void ResetDeviceUsage()
        {
            lock (_lock) { _deviceBytes.Clear(); }
        }

        public string GetMacCached(string ip)
        {
            lock (_lock)
            {
                if (_macCache.TryGetValue(ip, out string? cached)) return cached;
            }
            string mac = ResolveMac(ip);
            lock (_lock) { _macCache[ip] = mac; }
            return mac;
        }

        private void Loop()
        {
            byte[] packet = new byte[PacketBufSize];
            byte[] addr = new byte[AddrSize];

            while (_running)
            {
                try
                {
                    bool ok = WinDivertNative.WinDivertRecv(_handle, packet, (uint)packet.Length, out uint recvLen, addr);
                    if (!ok || recvLen == 0)
                    {
                        if (!_running) break;
                        Thread.Sleep(5);
                        continue;
                    }

                    Interlocked.Increment(ref TotalSeen);
                    bool reinject = ProcessPacket(packet, (int)recvLen);

                    if (reinject)
                    {
                        WinDivertNative.WinDivertSend(_handle, packet, recvLen, out _, addr);
                    }
                    else
                    {
                        Interlocked.Increment(ref TotalDropped);
                    }
                }
                catch
                {
                    if (!_running) break;
                    Thread.Sleep(10);
                }
            }
        }

        private bool ProcessPacket(byte[] packet, int len)
        {
            try
            {
                if (len < 20) return true;
                if ((packet[0] >> 4) != 4) return true; // yalnızca IPv4

                int totalLen = (packet[2] << 8) | packet[3];
                if (totalLen <= 0 || totalLen > len) totalLen = len;

                bool srcClient = IsHotspotClient(packet, 12);
                bool dstClient = IsHotspotClient(packet, 16);
                if (!srcClient && !dstClient) return true;

                string clientIp = srcClient
                    ? $"{packet[12]}.{packet[13]}.{packet[14]}.{packet[15]}"
                    : $"{packet[16]}.{packet[17]}.{packet[18]}.{packet[19]}";
                bool toClient = dstClient;

                // Kullanıcı politikası
                var (username, mbpsU, quotaU, usedU) = _userManager.GetClientPolicy(clientIp);
                if (quotaU > 0 && usedU >= quotaU)
                    return false; // veri kotası dolmuş -> düşür (UI oturumu kapatır)

                // Cihaz politikası (MAC)
                string mac = GetMacCached(clientIp);
                DeviceLimit? devLimit = !string.IsNullOrEmpty(mac) && mac != "-" ? _deviceLimitForMac(mac) : null;

                long devUsed = 0;
                if (devLimit != null && !string.IsNullOrEmpty(mac))
                {
                    lock (_lock) { _deviceBytes.TryGetValue(mac, out devUsed); }
                    if (devLimit.Bytes > 0 && devUsed >= devLimit.Bytes)
                        return false; // cihaz veri kotası dolmuş
                }

                // Sayaçlar
                _userManager.AddUsageByIp(clientIp, totalLen);
                if (!string.IsNullOrEmpty(mac) && mac != "-")
                {
                    lock (_lock) { _deviceBytes[mac] = _deviceBytes.TryGetValue(mac, out long b) ? b + totalLen : totalLen; }
                }

                // Hız: en kısıtlayıcı olan (kullanıcı ∩ cihaz)
                double eff = 0;
                if (mbpsU > 0) eff = mbpsU;
                if (devLimit != null && devLimit.Mbps > 0) eff = eff > 0 ? Math.Min(eff, devLimit.Mbps) : devLimit.Mbps;
                if (eff <= 0) return true;

                string key = clientIp + (toClient ? "|D" : "|U");
                double rateBps = eff * 125_000.0;
                double capacity = Math.Max(rateBps * 0.5, 65536);
                DateTime now = DateTime.UtcNow;

                lock (_lock)
                {
                    if (!_buckets.TryGetValue(key, out TokenBucket? bucket))
                    {
                        bucket = new TokenBucket { Tokens = capacity, Last = now };
                        _buckets[key] = bucket;
                    }
                    double elapsed = Math.Max(0, (now - bucket.Last).TotalSeconds);
                    bucket.Tokens = Math.Min(capacity, bucket.Tokens + elapsed * rateBps);
                    bucket.Last = now;
                    if (bucket.Tokens >= totalLen)
                    {
                        bucket.Tokens -= totalLen;
                        return true;
                    }
                    return false; // kova boş -> düşür (TCP yeniden gönderir, hız kabaca sınırlanır)
                }
            }
            catch
            {
                return true; // şüphede paketi geçir
            }
        }

        private static bool IsHotspotClient(byte[] packet, int offset)
        {
            // 192.168.137.x, ağ/broadcast ve gateway (.1) hariç
            return packet[offset] == 192 && packet[offset + 1] == 168 &&
                   packet[offset + 2] == 137 && packet[offset + 3] > 1 &&
                   packet[offset + 3] < 255;
        }

        private static string ResolveMac(string ip)
        {
            try
            {
                if (System.Net.IPAddress.TryParse(ip, out var ipAddr))
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
            catch { }
            return "-";
        }

        private sealed class TokenBucket
        {
            public double Tokens;
            public DateTime Last;
        }
    }
}
