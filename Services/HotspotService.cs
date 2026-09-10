using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using Win11HotspotManager.Models;

namespace Win11HotspotManager.Services
{
    public enum HotspotState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Error
    }

    public class HotspotService
    {
        private NetworkOperatorTetheringManager? _tetheringManager;
        private ConnectionProfile? _connectionProfile;
        private System.Threading.Timer? _peerMonitorTimer;

        public event Action<HotspotState, string>? StateChanged;
        public event Action<List<ConnectedClient>>? ClientsUpdated;
        public event Action<string>? ProfileUpdated;

        public HotspotState CurrentState { get; private set; } = HotspotState.Stopped;
        public string CurrentSsid { get; private set; } = "Windows11_Hotspot";
        public string CurrentPassword { get; private set; } = "12345678";
        public string ActiveProfileName { get; private set; } = "Bilinmiyor";

        public HotspotService()
        {
            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        }

        private void OnNetworkStatusChanged(object? sender)
        {
            try
            {
                InitializeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore background network status change errors
            }
        }

        public async Task<(bool Success, string Message)> InitializeAsync()
        {
            try
            {
                _connectionProfile = NetworkInformation.GetInternetConnectionProfile();
                if (_connectionProfile == null)
                {
                    ActiveProfileName = "İnternet Bağlantısı Yok";
                    ProfileUpdated?.Invoke(ActiveProfileName);
                    return (false, "Aktif bir internet bağlantısı (Wi-Fi veya Ethernet) bulunamadı.");
                }

                ActiveProfileName = _connectionProfile.ProfileName;
                ProfileUpdated?.Invoke(ActiveProfileName);

                var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(_connectionProfile);
                if (capability != TetheringCapability.Enabled)
                {
                    return (false, $"Mobil Erişim Noktası bu ağda kullanılamıyor (Durum: {capability}). Wi-Fi adaptörünüzün açık olduğundan emin olun.");
                }

                _tetheringManager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(_connectionProfile);

                var config = _tetheringManager.GetCurrentAccessPointConfiguration();
                if (!string.IsNullOrEmpty(config.Ssid))
                {
                    CurrentSsid = config.Ssid;
                }
                if (!string.IsNullOrEmpty(config.Passphrase))
                {
                    CurrentPassword = config.Passphrase;
                }

                if (_tetheringManager.TetheringOperationalState == TetheringOperationalState.On)
                {
                    UpdateState(HotspotState.Running, "Wi-Fi Hotspot yayında");
                    StartClientMonitoring();
                }
                else
                {
                    UpdateState(HotspotState.Stopped, "Hazır");
                }

                return (true, "Başarıyla hazırlandı.");
            }
            catch (Exception ex)
            {
                UpdateState(HotspotState.Error, $"Hata: {ex.Message}");
                return (false, $"Başlatma hatası: {ex.Message}");
            }
        }

        public async Task<(bool Success, string Message)> StartHotspotAsync(string ssid, string password)
        {
            if (string.IsNullOrWhiteSpace(ssid))
            {
                return (false, "Wi-Fi ağ adı (SSID) boş olamaz.");
            }

            if (string.IsNullOrEmpty(password) || password.Length < 8)
            {
                return (false, "Wi-Fi şifresi en az 8 karakter olmalıdır (WPA2 standardı).");
            }

            try
            {
                UpdateState(HotspotState.Starting, "Wi-Fi Hotspot başlatılıyor...");

                if (_connectionProfile == null || _tetheringManager == null)
                {
                    var initResult = await InitializeAsync();
                    if (!initResult.Success || _tetheringManager == null)
                    {
                        UpdateState(HotspotState.Error, initResult.Message);
                        return initResult;
                    }
                }

                // Yapılandırmayı güncelle
                var config = _tetheringManager.GetCurrentAccessPointConfiguration();
                config.Ssid = ssid;
                config.Passphrase = password;
                config.Band = TetheringWiFiBand.Auto;

                await _tetheringManager.ConfigureAccessPointAsync(config);
                CurrentSsid = ssid;
                CurrentPassword = password;

                // Hotspot'ı başlat
                var result = await _tetheringManager.StartTetheringAsync();

                if (result.Status == TetheringOperationStatus.Success)
                {
                    UpdateState(HotspotState.Running, "Wi-Fi Hotspot yayında");
                    StartClientMonitoring();
                    return (true, "Wi-Fi yayını başarıyla başlatıldı.");
                }
                else
                {
                    string errorMsg = result.Status switch
                    {
                        TetheringOperationStatus.WiFiDeviceOff => "Wi-Fi adaptörünüz kapalı. Lütfen Wi-Fi'ı açın.",
                        TetheringOperationStatus.MobileBroadbandDeviceOff => "Mobil geniş bant aygıtı kapalı.",
                        TetheringOperationStatus.EntitlementCheckFailure => "Operatör paylaşım izni kontrolü başarısız oldu.",
                        _ => $"Hotspot başlatılamadı (Durum: {result.Status})"
                    };

                    UpdateState(HotspotState.Error, errorMsg);
                    return (false, errorMsg);
                }
            }
            catch (Exception ex)
            {
                string errMsg = $"Beklenmeyen hata: {ex.Message}";
                UpdateState(HotspotState.Error, errMsg);
                return (false, errMsg);
            }
        }

        public async Task<(bool Success, string Message)> StopHotspotAsync()
        {
            try
            {
                UpdateState(HotspotState.Stopping, "Wi-Fi Hotspot durduruluyor...");
                StopClientMonitoring();

                if (_tetheringManager != null)
                {
                    var result = await _tetheringManager.StopTetheringAsync();
                    if (result.Status == TetheringOperationStatus.Success)
                    {
                        UpdateState(HotspotState.Stopped, "Wi-Fi Hotspot durduruldu.");
                        ClientsUpdated?.Invoke(new List<ConnectedClient>());
                        return (true, "Wi-Fi yayını durduruldu.");
                    }
                    else
                    {
                        UpdateState(HotspotState.Stopped, $"Durduruldu (Durum: {result.Status})");
                        return (true, "Durduruldu.");
                    }
                }

                UpdateState(HotspotState.Stopped, "Durduruldu.");
                return (true, "Durduruldu.");
            }
            catch (Exception ex)
            {
                UpdateState(HotspotState.Error, $"Durdurma hatası: {ex.Message}");
                return (false, ex.Message);
            }
        }

        private void StartClientMonitoring()
        {
            StopClientMonitoring();
            _peerMonitorTimer = new System.Threading.Timer(async _ =>
            {
                await RefreshClientsAsync();
            }, null, 1000, 2500);
        }

        private void StopClientMonitoring()
        {
            _peerMonitorTimer?.Dispose();
            _peerMonitorTimer = null;
        }

        public async Task RefreshClientsAsync()
        {
            if (_tetheringManager == null || CurrentState != HotspotState.Running)
            {
                return;
            }

            try
            {
                var peers = _tetheringManager.GetTetheringClients();
                var clientList = new List<ConnectedClient>();

                if (peers != null)
                {
                    foreach (var peer in peers)
                    {
                        var client = new ConnectedClient
                        {
                            MacAddress = peer.MacAddress ?? "-",
                            Status = "Bağlı"
                        };

                        if (peer.HostNames != null && peer.HostNames.Count > 0)
                        {
                            foreach (var h in peer.HostNames)
                            {
                                if (h.Type == Windows.Networking.HostNameType.Ipv4)
                                {
                                    client.IpAddress = h.CanonicalName;
                                }
                                else if (h.Type == Windows.Networking.HostNameType.DomainName && client.DeviceName == "Bilinmeyen Cihaz")
                                {
                                    client.DeviceName = h.DisplayName;
                                }
                            }

                            if (client.DeviceName == "Bilinmeyen Cihaz")
                            {
                                var firstHost = peer.HostNames.FirstOrDefault();
                                if (firstHost != null && !string.IsNullOrEmpty(firstHost.DisplayName))
                                {
                                    client.DeviceName = firstHost.DisplayName;
                                }
                            }
                        }

                        clientList.Add(client);
                    }
                }

                ClientsUpdated?.Invoke(clientList);
            }
            catch
            {
                // Silently ignore background polling exceptions
            }
        }

        private void UpdateState(HotspotState state, string message)
        {
            CurrentState = state;
            StateChanged?.Invoke(state, message);
        }
    }
}
