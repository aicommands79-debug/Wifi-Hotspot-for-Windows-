using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace Win11HotspotManager
{
    public partial class QrCodeWindow : Window
    {
        public string Ssid { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string PortalUrl { get; set; } = "http://192.168.137.1:8080";

        public QrCodeWindow()
        {
            InitializeComponent();
            Loaded += QrCodeWindow_Loaded;
        }

        private void QrCodeWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string wifiPayload = $"WIFI:T:WPA;S:{EscapeWifi(Ssid)};P:{EscapeWifi(Password)};;";
                ImgWifiQr.Source = RenderQr(wifiPayload);
                TxtWifiInfo.Text = $"Ağ: {Ssid}";

                ImgPortalQr.Source = RenderQr(PortalUrl);
                TxtPortalInfo.Text = PortalUrl;
            }
            catch (Exception ex)
            {
                TxtWifiInfo.Text = $"QR üretilemedi: {ex.Message}";
            }
        }

        private static string EscapeWifi(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace(":", "\\:");
        }

        private static BitmapImage RenderQr(string payload)
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            var png = new BitmapByteQRCode(data);
            byte[] bytes = png.GetGraphic(20);

            var img = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
