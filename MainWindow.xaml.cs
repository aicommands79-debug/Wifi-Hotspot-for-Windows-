using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Win11HotspotManager.Models;
using Win11HotspotManager.Services;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace Win11HotspotManager
{
    public partial class MainWindow : Window
    {
        private readonly HotspotService _hotspotService;
        private readonly UserManager _userManager;
        private readonly CaptivePortalServer _captivePortalServer;
        private readonly DnsGatingServer _dnsGatingServer;

        private Forms.NotifyIcon? _notifyIcon;
        private Forms.ContextMenuStrip? _trayMenu;
        private Forms.Form? _trayHelperForm;
        private bool _isExplicitExit = false;
        private bool _firstMinimizeNotice = true;
        private bool _isPasswordShown = false;

        private readonly UsageTracker _usageTracker = new();
        private readonly DeviceLimitStore _deviceLimits = new();
        private readonly PortalSettingsStore _portalSettings = new();
        private readonly BlocklistStore _blocklist = new();
        private TrafficMeter? _trafficMeter;
        private System.Windows.Threading.DispatcherTimer? _schedTimer;
        private bool _schedulerOwned = false;
        private DateTime? _manualStopAt = null;
        private readonly ObservableCollection<DnsLogEntry> _dnsLog = new();
        private ICollectionView? _dnsView;
        private System.Windows.Threading.DispatcherTimer? _statsTimer;
        private System.Windows.Threading.DispatcherTimer? _quotaTimer;

        public MainWindow()
        {
            InitializeComponent();

            _userManager = new UserManager();
            _captivePortalServer = new CaptivePortalServer(_userManager);
            _dnsGatingServer = new DnsGatingServer(_userManager);

            _hotspotService = new HotspotService();
            _hotspotService.StateChanged += OnHotspotStateChanged;
            _hotspotService.ClientsUpdated += OnClientsUpdated;
            _hotspotService.ProfileUpdated += OnProfileUpdated;

            _userManager.UsersChanged += OnUsersChanged;
            _captivePortalServer.LoginAttempted += OnLoginAttempted;
            _dnsGatingServer.DnsQueried += OnDnsQueried;
            _dnsGatingServer.BlocklistChecker = domain => _blocklist.IsBlocked(domain);
            _captivePortalServer.SettingsProvider = () => _portalSettings.Get();
            _trafficMeter = new TrafficMeter(_userManager, mac => _deviceLimits.Get(mac));

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            InitializeTrayIcon();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadAuthMode();
            RefreshUsersList();
            UpdateModeVisuals();

            GridDnsLog.ItemsSource = _dnsLog;
            _dnsView = CollectionViewSource.GetDefaultView(_dnsLog);
            _dnsView.Filter = DnsFilterPredicate;
            GridUsers.SelectionChanged += GridUsers_SelectionChanged;
            GridActivity.SelectionChanged += GridActivity_SelectionChanged;
            UpdateDriverStatus();

            var ps = _portalSettings.Get();
            TxtBusinessName.Text = ps.BusinessName;
            TxtAnnouncement.Text = ps.Announcement;
            RefreshBlocklist();

            ChkSchedEnabled.IsChecked = AppSettings.SchedEnabled;
            TxtSchedStart.Text = AppSettings.SchedStart;
            TxtSchedStop.Text = AppSettings.SchedStop;
            UpdateSchedStatus();

            _schedTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(60)
            };
            _schedTimer.Tick += SchedTimer_Tick;
            _schedTimer.Start();

            _statsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _statsTimer.Tick += StatsTimer_Tick;
            _statsTimer.Start();

            _quotaTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _quotaTimer.Tick += QuotaTimer_Tick;
            _quotaTimer.Start();

            var initResult = await _hotspotService.InitializeAsync();
            TxtInternetSource.Text = _hotspotService.ActiveProfileName;
            TxtSsid.Text = _hotspotService.CurrentSsid;
            PbPassword.Password = _hotspotService.CurrentPassword;
            TxtPasswordVisible.Text = _hotspotService.CurrentPassword;

            if (!initResult.Success)
            {
                ShowAlert(initResult.Message, isError: true);
            }
        }

        private void InitializeTrayIcon()
        {
            try
            {
                _notifyIcon = new Forms.NotifyIcon
                {
                    Text = "Windows 11 Wi-Fi Hotspot & Web Portalı",
                    Visible = true,
                    Icon = System.Drawing.SystemIcons.Application
                };

                // Gizli yardımcı pencere: sağ-tık menüsü açılmadan önce öne alınır,
                // yoksa menü odaklanamaz ve ekranda asılı kalır.
                _trayHelperForm = new Forms.Form
                {
                    ShowInTaskbar = false,
                    WindowState = Forms.FormWindowState.Minimized,
                    Opacity = 0
                };
                _ = _trayHelperForm.Handle;

                _trayMenu = new Forms.ContextMenuStrip();
                _trayMenu.Items.Add("Pencereyi Göster", null, (s, e) => ShowWindow());
                _trayMenu.Items.Add("Hotspot'ı Durdur", null, async (s, e) =>
                {
                    await StopAllServicesAsync();
                });
                _trayMenu.Items.Add(new Forms.ToolStripSeparator());
                _trayMenu.Items.Add("Uygulamadan Çık", null, (s, e) =>
                {
                    _isExplicitExit = true;
                    Close();
                });

                _notifyIcon.MouseUp += NotifyIcon_MouseUp;
                _notifyIcon.DoubleClick += (s, e) => ShowWindow();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tray icon initialization error: {ex.Message}");
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private void NotifyIcon_MouseUp(object? sender, Forms.MouseEventArgs e)
        {
            if (e.Button != Forms.MouseButtons.Right || _trayMenu == null) return;
            try
            {
                if (_trayHelperForm != null)
                    SetForegroundWindow(_trayHelperForm.Handle);
                _trayMenu.Show(Forms.Cursor.Position);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tray menu error: {ex.Message}");
            }
        }

        private void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExplicitExit)
            {
                e.Cancel = true;
                Hide();

                if (_firstMinimizeNotice && _notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(
                        3000, 
                        "Wi-Fi Hotspot Arka Planda", 
                        "Hotspot ve Web Portalı arka planda kesintisiz çalışmaya devam ediyor.", 
                        Forms.ToolTipIcon.Info
                    );
                    _firstMinimizeNotice = false;
                }
            }
            else
            {
                try { _statsTimer?.Stop(); } catch { }
                try { _quotaTimer?.Stop(); } catch { }
                StopAllServicesAsync().Wait();
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
                try { _trayMenu?.Dispose(); } catch { }
                try { _trayHelperForm?.Dispose(); } catch { }
            }
        }

        private async void BtnToggleHotspot_Click(object sender, RoutedEventArgs e)
        {
            HideAlert();

            if (_hotspotService.CurrentState == HotspotState.Running)
            {
                BtnToggleHotspot.IsEnabled = false;
                await StopAllServicesAsync(manual: true);
                BtnToggleHotspot.IsEnabled = true;
            }
            else
            {
                string ssid = TxtSsid.Text.Trim();
                string password = _isPasswordShown ? TxtPasswordVisible.Text : PbPassword.Password;

                if (string.IsNullOrWhiteSpace(ssid))
                {
                    ShowAlert("Lütfen geçerli bir Wi-Fi ağ adı (SSID) girin.", isError: true);
                    return;
                }

                if (string.IsNullOrEmpty(password) || password.Length < 8)
                {
                    ShowAlert("Wi-Fi şifresi en az 8 karakter olmalıdır.", isError: true);
                    return;
                }

                BtnToggleHotspot.IsEnabled = false;
                var res = await _hotspotService.StartHotspotAsync(ssid, password);
                BtnToggleHotspot.IsEnabled = true;
                _schedulerOwned = false;

                if (res.Success)
                {
                    _trafficMeter?.Start();
                    UpdateDriverStatus();
                    if (RbPortalMode.IsChecked == true)
                    {
                        StartPortalServices();
                    }
                }
                else
                {
                    ShowAlert(res.Message, isError: true);
                }
            }
        }

        private async Task StopAllServicesAsync(bool manual = true)
        {
            if (manual) _manualStopAt = DateTime.Now;
            try { _trafficMeter?.Stop(); } catch { }
            _captivePortalServer.Stop();
            _dnsGatingServer.Stop();
            _userManager.FlushUsage();
            await _hotspotService.StopHotspotAsync();
            try
            {
                if (Dispatcher.CheckAccess()) UpdateDriverStatus();
                else Dispatcher.Invoke(UpdateDriverStatus);
            }
            catch { }
        }

        /// <summary>
        /// Portal + DNS gaspını firewall izinleriyle birlikte başlatır.
        /// Başarısız olursa (admin yok / port dolu) kullanıcıya net hata gösterir.
        /// </summary>
        private void StartPortalServices()
        {
            try { FirewallHelper.EnsurePortalRules(); } catch { }

            _captivePortalServer.Start();
            bool dnsOk = _dnsGatingServer.Start();

            if (!_captivePortalServer.IsRunning)
            {
                ShowAlert($"Web portalı başlatılamadı: {_captivePortalServer.LastError} Programı Yönetici olarak çalıştırın.", isError: true);
                return;
            }

            if (!dnsOk)
            {
                ShowAlert($"Web portalı açık (8080) ama DNS gaspı çalışmıyor: {_dnsGatingServer.LastError} Otomatik açılır ekran çalışmayabilir, misafir http://192.168.137.1:8080 adresini elle açsın. Yönetici olarak çalıştırın.", isError: true);
                return;
            }

            if (!_captivePortalServer.Port80Active)
            {
                ShowAlert($"Portal çalışıyor (:8080 açık, 80 kapalı: {_captivePortalServer.LastError}). Çoğu telefonda otomatik ekran yine açılır; açılmazsa misafir http://192.168.137.1:8080 adresine girsin.", isError: false);
            }
            else
            {
                ShowAlert("🌐 Portal aktif: cihaza bağlanan misafirde giriş ekranı otomatik açılacak.", isError: false);
            }
        }

        private void CardPortalMode_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_hotspotService.CurrentState == HotspotState.Running ||
                _hotspotService.CurrentState == HotspotState.Starting)
            {
                ShowAlert("Yayın aktifken giriş yöntemi değiştirilemez. Önce Hotspot'ı durdurun.", isError: true);
                return;
            }
            RbPortalMode.IsChecked = true;
        }

        private void CardStandardMode_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_hotspotService.CurrentState == HotspotState.Running ||
                _hotspotService.CurrentState == HotspotState.Starting)
            {
                ShowAlert("Yayın aktifken giriş yöntemi değiştirilemez. Önce Hotspot'ı durdurun.", isError: true);
                return;
            }
            RbStandardMode.IsChecked = true;
        }

        private void AuthMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _isLoadingSettings) return;
            HideAlert();
            UpdateModeVisuals();
        }

        private bool _isLoadingSettings = false;

        private void LoadAuthMode()
        {
            _isLoadingSettings = true;
            try
            {
                AppSettings.Load();
                if (AppSettings.AuthMode.Equals("Standard", StringComparison.OrdinalIgnoreCase))
                    RbStandardMode.IsChecked = true;
                else
                    RbPortalMode.IsChecked = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Settings load error: {ex.Message}");
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        private void SaveAuthMode(bool isPortal)
        {
            if (_isLoadingSettings) return;
            AppSettings.AuthMode = isPortal ? "Portal" : "Standard";
            AppSettings.Save();
        }

        private void UpdateModeVisuals()
        {
            bool isPortal = RbPortalMode.IsChecked == true;
            SaveAuthMode(isPortal);

            if (isPortal)
            {
                // Web Portal Mode Active
                CardPortalMode.BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Blue
                CardPortalMode.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                CardPortalMode.Opacity = 1.0;
                BadgePortalActive.Visibility = Visibility.Visible;

                CardStandardMode.BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)); // Dim Gray
                CardStandardMode.Background = new SolidColorBrush(Color.FromRgb(30, 30, 34));
                CardStandardMode.Opacity = 0.65;
                BadgeStandardActive.Visibility = Visibility.Collapsed;

                BoxPortalInfo.Visibility = Visibility.Visible;
                CardPortalLook.Visibility = Visibility.Visible;
                BoxStandardModeWarning.Visibility = Visibility.Collapsed;
                BoxUserActions.Opacity = 1.0;
                BoxUserActions.IsEnabled = true;
                TabUsers.Opacity = 1.0;
                LblPassword.Text = "Temel Wi-Fi Parolası";
                LblPasswordHelp.Text = "Herkese verilen ortak şifre (telefon bir kez girer, hatırlar). Asıl koruma web girişidir.";

                if (_hotspotService.CurrentState != HotspotState.Running && 
                    _hotspotService.CurrentState != HotspotState.Starting && 
                    _hotspotService.CurrentState != HotspotState.Stopping)
                {
                    BtnToggleHotspot.Content = "🌐 Web Arayüzü ile Başlat";
                    BtnToggleHotspot.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); // Blue
                }

                if (_hotspotService.CurrentState == HotspotState.Running)
                {
                    StartPortalServices();
                }
            }
            else
            {
                // Standard Password Mode Active
                CardStandardMode.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Emerald Green
                CardStandardMode.Background = new SolidColorBrush(Color.FromRgb(6, 78, 59));
                CardStandardMode.Opacity = 1.0;
                BadgeStandardActive.Visibility = Visibility.Visible;

                CardPortalMode.BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)); // Dim Gray
                CardPortalMode.Background = new SolidColorBrush(Color.FromRgb(30, 30, 34));
                CardPortalMode.Opacity = 0.65;
                BadgePortalActive.Visibility = Visibility.Collapsed;

                BoxPortalInfo.Visibility = Visibility.Collapsed;
                CardPortalLook.Visibility = Visibility.Collapsed;
                BoxStandardModeWarning.Visibility = Visibility.Visible;
                // Kullanıcı/bilet üretimi yalnızca portal modunda anlamlıdır:
                // standart modda listeyi salt görüntülenir yap, ekleme butonlarını pasifleştir.
                BoxUserActions.Opacity = 0.55;
                BoxUserActions.IsEnabled = false;
                TabUsers.Opacity = 1.0;
                LblPassword.Text = "Wi-Fi Parolası";
                LblPasswordHelp.Text = "Klasik WPA2 şifresi. Cihazlar bu şifreyi girerek doğrudan internete bağlanır.";

                if (_hotspotService.CurrentState != HotspotState.Running && 
                    _hotspotService.CurrentState != HotspotState.Starting && 
                    _hotspotService.CurrentState != HotspotState.Stopping)
                {
                    BtnToggleHotspot.Content = "🔑 Şifreli Yayını Başlat";
                    BtnToggleHotspot.Background = new SolidColorBrush(Color.FromRgb(5, 150, 105)); // Green
                }

                _captivePortalServer.Stop();
                _dnsGatingServer.Stop();
            }
        }

        private void BtnTogglePassword_Click(object sender, RoutedEventArgs e)
        {
            if (_isPasswordShown)
            {
                PbPassword.Password = TxtPasswordVisible.Text;
                TxtPasswordVisible.Visibility = Visibility.Collapsed;
                PbPassword.Visibility = Visibility.Visible;
                BtnTogglePassword.Content = "👁️";
                _isPasswordShown = false;
            }
            else
            {
                TxtPasswordVisible.Text = PbPassword.Password;
                PbPassword.Visibility = Visibility.Collapsed;
                TxtPasswordVisible.Visibility = Visibility.Visible;
                BtnTogglePassword.Content = "🔒";
                _isPasswordShown = true;
            }
        }

        private void BtnTestPortal_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StartPortalServices();
                string url = _captivePortalServer.Port8080Active || _captivePortalServer.Port80Active
                    ? "http://192.168.137.1:8080"
                    : "http://localhost:8080";
                // Yerel testte portal IP'si yoksa localhost'a düş
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "http://localhost:8080",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                ShowAlert($"Tarayıcı açılamadı: {ex.Message}", isError: true);
            }
        }

        #region User Management Handlers

        private void RefreshUsersList()
        {
            Dispatcher.Invoke(() =>
            {
                var users = _userManager.Users;
                GridUsers.ItemsSource = null;
                GridUsers.ItemsSource = users;
                TxtTotalUsersCount.Text = $"{users.Count} Kullanıcı";
            });
        }

        private void OnUsersChanged()
        {
            RefreshUsersList();
        }

        private void OnLoginAttempted(string username, string ip, bool success)
        {
            Dispatcher.Invoke(() =>
            {
                if (success)
                {
                    ShowAlert($"✅ '{username}' kullanıcısı ({ip}) başarıyla giriş yaptı!", isError: false);
                }
                else
                {
                    ShowAlert($"⚠️ '{username}' kullanıcısı ({ip}) için hatalı giriş veya cihaz çakışması tespit edildi.", isError: true);
                }
            });
        }

        private void BtnGenerateRandomUser_Click(object sender, RoutedEventArgs e)
        {
            if (RbStandardMode.IsChecked == true)
            {
                ShowAlert("Klasik Wi-Fi Şifresi modundasınız. Bilet/kullanıcı üretimi yalnızca Web Giriş Portalı modunda kullanılır.", isError: true);
                MainTabs.SelectedItem = MainTabs.Items[0];
                return;
            }
            var user = _userManager.GenerateRandomUser();
            string clipboardText = $"Wi-Fi Giriş Bilgileri:\nKullanıcı Adı: {user.Username}\nŞifre: {user.Password}\nGiriş Sayfası: http://192.168.137.1:8080";
            
            try
            {
                System.Windows.Clipboard.SetText(clipboardText);
                ShowAlert($"🎉 Yeni Bilet Üretildi: '{user.Username}' (Şifre: {user.Password}) - Bilgiler panoya kopyalandı!", isError: false);
            }
            catch
            {
                ShowAlert($"Yeni Bilet Üretildi: '{user.Username}' (Şifre: {user.Password})", isError: false);
            }
        }

        private void BtnAddUser_Click(object sender, RoutedEventArgs e)
        {
            if (RbStandardMode.IsChecked == true)
            {
                ShowAlert("Klasik Wi-Fi Şifresi modundasınız. Kullanıcı ekleme yalnızca Web Giriş Portalı modunda kullanılır.", isError: true);
                return;
            }
            string username = TxtNewUsername.Text.Trim();
            string password = TxtNewPassword.Text.Trim();

            var res = _userManager.AddUser(username, password);
            if (res.Success)
            {
                ShowAlert($"✅ Kullanıcı '{username}' başarıyla eklendi.", isError: false);
                TxtNewUsername.Text = $"misafir_{new Random().Next(100, 999)}";
                TxtNewPassword.Text = $"Pass{new Random().Next(1000, 9999)}";
            }
            else
            {
                ShowAlert(res.Message, isError: true);
            }
        }

        private void BtnUserQr_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is PortalUser user)
            {
                var wnd = new QrCodeWindow
                {
                    Owner = this,
                    TicketUsername = user.Username,
                    TicketPassword = user.Password,
                    PortalUrl = "http://192.168.137.1:8080"
                };
                wnd.ShowDialog();
            }
        }

        private void BtnCopyUser_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is PortalUser user)
            {
                string text = $"Kullanıcı Adı: {user.Username}\nŞifre: {user.Password}";
                try
                {
                    System.Windows.Clipboard.SetText(text);
                    ShowAlert($"📋 '{user.Username}' bilgileri panoya kopyalandı.", isError: false);
                }
                catch { }
            }
        }

        private void BtnResetUser_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is PortalUser user)
            {
                _userManager.ResetBinding(user.Username);
                ShowAlert($"⚡ '{user.Username}' kullanıcısının cihaz kilidi kaldırıldı ve oturumu sıfırlandı.", isError: false);
            }
        }

        private void BtnDeleteUser_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is PortalUser user)
            {
                var result = MessageBox.Show($"'{user.Username}' kullanıcısını silmek istediğinize emin misiniz?", "Kullanıcıyı Sil", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _userManager.RemoveUser(user.Username);
                    ShowAlert($"🗑️ '{user.Username}' silindi.", isError: false);
                }
            }
        }

        #endregion

        #region New Features (QR, Bulk, Look, Blocklist, Scheduler)

        private void BtnShowQr_Click(object sender, RoutedEventArgs e)
        {
            string ssid = TxtSsid.Text.Trim();
            string password = _isPasswordShown ? TxtPasswordVisible.Text : PbPassword.Password;
            if (string.IsNullOrWhiteSpace(ssid))
            {
                ShowAlert("Önce bir Wi-Fi ağ adı (SSID) girin.", isError: true);
                return;
            }
            var wnd = new QrCodeWindow
            {
                Owner = this,
                Ssid = ssid,
                Password = password,
                PortalUrl = "http://192.168.137.1:8080"
            };
            wnd.ShowDialog();
        }

        private void BtnBulkTickets_Click(object sender, RoutedEventArgs e)
        {
            if (RbStandardMode.IsChecked == true)
            {
                ShowAlert("Klasik Wi-Fi Şifresi modundasınız. Bilet üretimi yalnızca Web Giriş Portalı modunda kullanılır.", isError: true);
                return;
            }
            var wnd = new BulkTicketsWindow(_userManager)
            {
                Owner = this,
                PortalUrl = "http://192.168.137.1:8080"
            };
            wnd.ShowDialog();
        }

        private void BtnSavePortalLook_Click(object sender, RoutedEventArgs e)
        {
            _portalSettings.Update(TxtBusinessName.Text, TxtAnnouncement.Text);
            ShowAlert("🎨 Giriş sayfası güncellendi. Telefonda sayfayı yenileyin.", isError: false);
        }

        private void RefreshBlocklist()
        {
            Dispatcher.Invoke(() =>
            {
                var list = _blocklist.GetAll();
                LstBlocked.ItemsSource = null;
                LstBlocked.ItemsSource = list;
                TxtBlockCount.Text = $"{list.Count} site";
            });
        }

        private void BtnAddBlock_Click(object sender, RoutedEventArgs e)
        {
            string domain = TxtBlockDomain.Text.Trim();
            if (_blocklist.Add(domain))
            {
                TxtBlockDomain.Text = string.Empty;
                RefreshBlocklist();
                ShowAlert($"🚫 '{domain}' engellendi.", isError: false);
            }
            else
            {
                ShowAlert("Geçersiz alan adı (örn: ornek.com) veya zaten listede.", isError: true);
            }
        }

        private void BtnRemoveBlock_Click(object sender, RoutedEventArgs e)
        {
            if (LstBlocked.SelectedItem is string domain && _blocklist.Remove(domain))
            {
                RefreshBlocklist();
                ShowAlert($"✅ '{domain}' engeli kaldırıldı.", isError: false);
            }
        }

        private void BtnSaveSched_Click(object sender, RoutedEventArgs e)
        {
            if (!TimeSpan.TryParse(TxtSchedStart.Text.Trim(), out _) || !TimeSpan.TryParse(TxtSchedStop.Text.Trim(), out _))
            {
                ShowAlert("Saatler SS:dd formatında olmalı (örn: 08:00).", isError: true);
                return;
            }
            AppSettings.SchedEnabled = ChkSchedEnabled.IsChecked == true;
            AppSettings.SchedStart = TxtSchedStart.Text.Trim();
            AppSettings.SchedStop = TxtSchedStop.Text.Trim();
            AppSettings.Save();
            UpdateSchedStatus();
            ShowAlert(AppSettings.SchedEnabled ? $"⏰ Zamanlayıcı açık: {AppSettings.SchedStart}–{AppSettings.SchedStop}." : "⏰ Zamanlayıcı kapatıldı.", isError: false);
        }

        private static bool IsInScheduleWindow(TimeSpan start, TimeSpan stop, TimeSpan now)
        {
            if (start == stop) return false;
            if (start < stop) return now >= start && now < stop;
            return now >= start || now < stop; // gece yarısını aşan aralık
        }

        private void UpdateSchedStatus()
        {
            try
            {
                if (!AppSettings.SchedEnabled)
                {
                    TxtSchedStatus.Text = "⏰ Zamanlayıcı kapalı";
                    return;
                }
                TimeSpan.TryParse(AppSettings.SchedStart, out TimeSpan start);
                TimeSpan.TryParse(AppSettings.SchedStop, out TimeSpan stop);
                bool inside = IsInScheduleWindow(start, stop, DateTime.Now.TimeOfDay);
                TxtSchedStatus.Text = $"⏰ {AppSettings.SchedStart}–{AppSettings.SchedStop} arası açık{(inside ? " (penceredesin)" : "")}";
            }
            catch { }
        }

        private async void SchedTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                UpdateSchedStatus();
                if (!AppSettings.SchedEnabled) return;
                if (!TimeSpan.TryParse(AppSettings.SchedStart, out TimeSpan start) ||
                    !TimeSpan.TryParse(AppSettings.SchedStop, out TimeSpan stop))
                    return;

                bool inside = IsInScheduleWindow(start, stop, DateTime.Now.TimeOfDay);

                if (inside && _hotspotService.CurrentState == HotspotState.Stopped)
                {
                    if (_manualStopAt.HasValue && (DateTime.Now - _manualStopAt.Value).TotalMinutes < 15)
                        return; // kullanıcı yeni durdurmuş, zorlama

                    string ssid = TxtSsid.Text.Trim();
                    string password = _isPasswordShown ? TxtPasswordVisible.Text : PbPassword.Password;
                    if (string.IsNullOrWhiteSpace(ssid) || string.IsNullOrEmpty(password) || password.Length < 8)
                        return; // geçersiz ayar, sessiz geç

                    var res = await _hotspotService.StartHotspotAsync(ssid, password);
                    if (res.Success)
                    {
                        _trafficMeter?.Start();
                        UpdateDriverStatus();
                        if (RbPortalMode.IsChecked == true) StartPortalServices();
                        _schedulerOwned = true;
                        ShowAlert("⏰ Zamanlayıcı: hotspot otomatik başlatıldı.", isError: false);
                    }
                }
                else if (!inside && _hotspotService.CurrentState == HotspotState.Running && _schedulerOwned)
                {
                    await StopAllServicesAsync(manual: false);
                    _schedulerOwned = false;
                    ShowAlert("⏰ Zamanlayıcı: mesai dışı, hotspot otomatik durduruldu.", isError: false);
                }
            }
            catch { }
        }

        #endregion

        #region Monitoring (DNS Log, Speed, Quota)

        private void OnDnsQueried(string clientIp, string domain, ushort qtype, bool allowed, bool blocked)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _userManager.TryGetUsernameByIp(clientIp, out string? uname);
                _usageTracker.RecordDns(clientIp, uname);
                _dnsLog.Add(new DnsLogEntry
                {
                    Time = DateTime.Now,
                    ClientIp = clientIp,
                    Username = uname ?? string.Empty,
                    Domain = string.IsNullOrEmpty(domain) ? $"(qtype {qtype})" : domain,
                    Allowed = allowed,
                    Blocked = blocked
                });
                while (_dnsLog.Count > 1000) _dnsLog.RemoveAt(0);
                TxtDnsTotal.Text = $"{_usageTracker.TotalDnsQueries} sorgu";
                TxtDnsLogCount.Text = $"{_dnsLog.Count} kayıt";
            });
        }

        private bool DnsFilterPredicate(object obj)
        {
            if (obj is not DnsLogEntry e) return false;
            string f = TxtDnsFilter?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(f)) return true;
            return e.Domain.Contains(f, StringComparison.OrdinalIgnoreCase)
                || e.ClientIp.Contains(f, StringComparison.OrdinalIgnoreCase)
                || e.Username.Contains(f, StringComparison.OrdinalIgnoreCase);
        }

        private void TxtDnsFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            _dnsView?.Refresh();
        }

        private void BtnClearDns_Click(object sender, RoutedEventArgs e)
        {
            _dnsLog.Clear();
            TxtDnsLogCount.Text = "0 kayıt";
        }

        private void BtnExportDns_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win11HotspotManager");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"dnslog_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                using var sw = new StreamWriter(path, false, new UTF8Encoding(true));
                sw.WriteLine("Saat;IP;Kullanici;AlanAdi;Sonuc");
                foreach (var entry in _dnsLog)
                {
                    sw.WriteLine($"{entry.Time:HH:mm:ss};{entry.ClientIp};{entry.Username};{entry.Domain};{(entry.Allowed ? "Iletildi" : "PortalaYonlendirildi")}");
                }
                ShowAlert($"📤 DNS logu aktarıldı: {path}", isError: false);
            }
            catch (Exception ex)
            {
                ShowAlert($"CSV aktarılamadı: {ex.Message}", isError: true);
            }
        }

        private void StatsTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                var (down, up, found) = _usageTracker.GetHotspotSpeed();
                TxtSpeedDown.Text = found ? $"{down:F1} Mbps" : "-";
                TxtSpeedUp.Text = found ? $"{up:F1} Mbps" : "-";
                TxtActiveSessions.Text = $"{_userManager.Users.Count(u => u.IsLoggedIn)} oturum";

                var rows = _usageTracker.GetActivity(_userManager);
                if (_trafficMeter != null && _trafficMeter.IsRunning)
                {
                    foreach (var row in rows)
                    {
                        try
                        {
                            row.Mac = _trafficMeter.GetMacCached(row.Ip);
                            if (!string.IsNullOrEmpty(row.Mac) && row.Mac != "-")
                                row.DataUsedBytes = _trafficMeter.GetDeviceBytes(row.Mac);
                        }
                        catch { }
                    }
                }
                GridActivity.ItemsSource = null;
                GridActivity.ItemsSource = rows;

                // Veri kotası dolanları kapat (hızlı kontrol, 2 sn)
                var dataExpired = _userManager.GetDataQuotaExpired();
                foreach (var name in dataExpired)
                    _userManager.Logout(name);
                if (dataExpired.Count > 0)
                    ShowAlert($"📦 Veri kotası doldu, oturum kapatıldı: {string.Join(", ", dataExpired)}", isError: true);
            }
            catch { }
        }

        private void QuotaTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                var expired = new List<string>();
                foreach (var u in _userManager.Users)
                {
                    if (u.IsLoggedIn && u.TimeQuotaMinutes > 0 && u.LiveUsedMinutes >= u.TimeQuotaMinutes)
                        expired.Add(u.Username);
                }
                foreach (var name in expired)
                    _userManager.Logout(name);
                if (expired.Count > 0)
                    ShowAlert($"⏱ Süre kotası doldu, oturum kapatıldı: {string.Join(", ", expired)}", isError: true);

                _userManager.FlushUsage();
            }
            catch { }
        }

        private void GridUsers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridUsers.SelectedItem is PortalUser sel)
                TxtQuotaTarget.Text = $"Hesap: {sel.Username} (süre: {sel.QuotaDisplay}, hız: {sel.SpeedDisplay}, veri: {sel.DataDisplay})";
            else
                TxtQuotaTarget.Text = "Hesap: -";
        }

        private void GridActivity_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridActivity.SelectedItem is ActivityRow row)
                TxtDeviceTarget.Text = $"Cihaz: {row.Ip} ({row.MacDisplay})";
            else
                TxtDeviceTarget.Text = "Cihaz: -";
        }

        private void UpdateDriverStatus()
        {
            try
            {
                if (_trafficMeter == null) { TxtDriverStatus.Text = "Sürücü: -"; return; }
                if (_trafficMeter.IsRunning) { TxtDriverStatus.Text = "Sürücü: ✔ Aktif"; TxtDriverStatus.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153)); }
                else if (!_trafficMeter.IsAvailable) { TxtDriverStatus.Text = "Sürücü: ⚠ Yok (hız/veri kotası pasif)"; TxtDriverStatus.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)); }
                else { TxtDriverStatus.Text = $"Sürücü: { _trafficMeter.Status}"; TxtDriverStatus.Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)); }
            }
            catch { }
        }

        private bool TryParseLimits(out double mbps, out long dataBytes, out int minutes)
        {
            mbps = 0; dataBytes = 0; minutes = 0;
            if (!double.TryParse(TxtSpeedMbps.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mbps) || mbps < 0 || mbps > 1000)
            {
                ShowAlert("Hız 0-1000 Mbps arası olmalı (0 = sınırsız).", isError: true);
                return false;
            }
            if (!double.TryParse(TxtDataGb.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gb) || gb < 0 || gb > 1000)
            {
                ShowAlert("Veri 0-1000 GB arası olmalı (0 = sınırsız).", isError: true);
                return false;
            }
            if (!int.TryParse(TxtQuotaMinutes.Text.Trim(), out minutes) || minutes < 0 || minutes > 10080)
            {
                ShowAlert("Süre 0-10080 dakika arası olmalı (0 = sınırsız).", isError: true);
                return false;
            }
            dataBytes = (long)(gb * 1_000_000_000.0);
            return true;
        }

        private void BtnApplyUserLimits_Click(object sender, RoutedEventArgs e)
        {
            if (GridUsers.SelectedItem is not PortalUser sel)
            {
                ShowAlert("Önce 'Kullanıcılar' listesinden bir hesap seçin.", isError: true);
                MainTabs.SelectedItem = TabUsers;
                return;
            }
            if (!TryParseLimits(out double mbps, out long dataBytes, out int minutes)) return;

            _userManager.SetSpeedLimit(sel.Username, mbps);
            _userManager.SetDataQuota(sel.Username, dataBytes);
            _userManager.SetTimeQuota(sel.Username, minutes);
            _trafficMeter?.ResetShapers();
            ShowAlert($"👤 '{sel.Username}' limitleri: hız {(mbps == 0 ? "∞" : mbps + " Mbps")}, veri {(dataBytes == 0 ? "∞" : PortalUser.FormatBytes(dataBytes))}, süre {(minutes == 0 ? "∞" : minutes + " dk")}.", isError: false);
        }

        private void BtnApplyDeviceLimits_Click(object sender, RoutedEventArgs e)
        {
            if (GridActivity.SelectedItem is not ActivityRow row)
            {
                ShowAlert("Önce aktivite listesinden bir cihaz seçin.", isError: true);
                return;
            }
            string mac = row.Mac;
            if (string.IsNullOrEmpty(mac) || mac == "-")
            {
                ShowAlert($"'{row.Ip}' için MAC çözülemedi. Cihaz aktifken tekrar deneyin.", isError: true);
                return;
            }
            if (!TryParseLimits(out double mbps, out long dataBytes, out int minutes)) return;

            _deviceLimits.Set(mac, mbps, dataBytes);
            _trafficMeter?.ResetShapers();
            string extra = minutes > 0 ? " (Not: süre kotası yalnızca hesaplara uygulanır, cihaza değil.)" : string.Empty;
            ShowAlert($"📱 '{mac}' limitleri: hız {(mbps == 0 ? "∞" : mbps + " Mbps")}, veri {(dataBytes == 0 ? "∞" : PortalUser.FormatBytes(dataBytes))}.{extra}", isError: false);
        }

        private void BtnClearLimits_Click(object sender, RoutedEventArgs e)
        {
            bool any = false;
            if (GridUsers.SelectedItem is PortalUser sel)
            {
                _userManager.SetSpeedLimit(sel.Username, 0);
                _userManager.SetDataQuota(sel.Username, 0);
                _userManager.SetTimeQuota(sel.Username, 0);
                any = true;
            }
            if (GridActivity.SelectedItem is ActivityRow row && !string.IsNullOrEmpty(row.Mac) && row.Mac != "-")
            {
                _deviceLimits.Clear(row.Mac);
                any = true;
            }
            _trafficMeter?.ResetShapers();
            ShowAlert(any ? "♾ Seçili hedefler sınırsız yapıldı." : "Önce bir hesap veya cihaz seçin.", isError: !any);
        }

        private void BtnResetUsage_Click(object sender, RoutedEventArgs e)
        {
            bool any = false;
            if (GridUsers.SelectedItem is PortalUser sel)
            {
                var res = _userManager.ResetUsage(sel.Username);
                ShowAlert(res.Success ? $"↺ '{sel.Username}' kullanımı sıfırlandı (süre + veri)." : res.Message, isError: !res.Success);
                any = true;
            }
            if (GridActivity.SelectedItem is ActivityRow row && !string.IsNullOrEmpty(row.Mac) && row.Mac != "-")
            {
                any = true; // cihaz sayacı oturumluk tutulur, uygulama yeniden başlayınca sıfırlanır
            }
            if (!any)
            {
                ShowAlert("Önce 'Kullanıcılar' listesinden bir hesap seçin.", isError: true);
                MainTabs.SelectedItem = TabUsers;
            }
        }

        #endregion

        #region Hardware Clients Handlers

        private async void BtnRefreshClients_Click(object sender, RoutedEventArgs e)
        {
            BtnRefreshClients.IsEnabled = false;
            await _hotspotService.RefreshClientsAsync();
            await Task.Delay(300);
            BtnRefreshClients.IsEnabled = true;
        }

        private void OnHotspotStateChanged(HotspotState state, string message)
        {
            Dispatcher.Invoke(() =>
            {
                switch (state)
                {
                    case HotspotState.Running:
                        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Green
                        TxtStatus.Text = "Yayında (Aktif)";
                        TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                        BadgeStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(22, 101, 52));
                        BadgeStatus.Background = new SolidColorBrush(Color.FromRgb(20, 40, 25));

                        BtnToggleHotspot.Content = "Hotspot'ı Durdur";
                        BtnToggleHotspot.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // Red

                        TxtSsid.IsEnabled = false;
                        PbPassword.IsEnabled = false;
                        TxtPasswordVisible.IsEnabled = false;
                        BtnTogglePassword.IsEnabled = false;
                        RbPortalMode.IsEnabled = false;
                        RbStandardMode.IsEnabled = false;
                        CardPortalMode.IsEnabled = false;
                        CardStandardMode.IsEnabled = false;
                        break;

                    case HotspotState.Starting:
                    case HotspotState.Stopping:
                        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
                        TxtStatus.Text = state == HotspotState.Starting ? "Başlatılıyor..." : "Durduruluyor...";
                        TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                        BadgeStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(146, 64, 14));
                        BadgeStatus.Background = new SolidColorBrush(Color.FromRgb(40, 30, 15));

                        BtnToggleHotspot.Content = TxtStatus.Text;
                        BtnToggleHotspot.IsEnabled = false;
                        break;

                    case HotspotState.Stopped:
                    case HotspotState.Error:
                        StatusDot.Fill = new SolidColorBrush(state == HotspotState.Error ? Color.FromRgb(239, 68, 68) : Color.FromRgb(156, 163, 175));
                        TxtStatus.Text = state == HotspotState.Error ? "Hata" : "Kapalı";
                        TxtStatus.Foreground = new SolidColorBrush(state == HotspotState.Error ? Color.FromRgb(248, 113, 113) : Color.FromRgb(209, 213, 219));
                        BadgeStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70));
                        BadgeStatus.Background = new SolidColorBrush(Color.FromRgb(39, 39, 42));

                        BtnToggleHotspot.IsEnabled = true;

                        TxtSsid.IsEnabled = true;
                        PbPassword.IsEnabled = true;
                        TxtPasswordVisible.IsEnabled = true;
                        BtnTogglePassword.IsEnabled = true;
                        RbPortalMode.IsEnabled = true;
                        RbStandardMode.IsEnabled = true;
                        CardPortalMode.IsEnabled = true;
                        CardStandardMode.IsEnabled = true;
                        UpdateModeVisuals();
                        break;
                }
            });
        }

        private void OnClientsUpdated(List<ConnectedClient> clients)
        {
            Dispatcher.Invoke(() =>
            {
                TxtClientsCount.Text = $"{clients.Count} Cihaz";

                if (clients.Count > 0)
                {
                    PanelEmptyClients.Visibility = Visibility.Collapsed;
                    GridClients.Visibility = Visibility.Visible;
                    GridClients.ItemsSource = null;
                    GridClients.ItemsSource = clients;
                }
                else
                {
                    GridClients.ItemsSource = null;
                    GridClients.Visibility = Visibility.Collapsed;
                    PanelEmptyClients.Visibility = Visibility.Visible;
                }
            });
        }

        private void OnProfileUpdated(string profileName)
        {
            Dispatcher.Invoke(() =>
            {
                TxtInternetSource.Text = profileName;
            });
        }

        private void ShowAlert(string message, bool isError)
        {
            TxtAlertIcon.Text = isError ? "⚠️" : "ℹ️";
            TxtAlertMessage.Text = message;
            BorderAlert.Visibility = Visibility.Visible;
        }

        private void HideAlert()
        {
            BorderAlert.Visibility = Visibility.Collapsed;
        }

        #endregion
    }
}