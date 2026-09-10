using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Win11HotspotManager.Models;
using Win11HotspotManager.Services;

namespace Win11HotspotManager
{
    public partial class BulkTicketsWindow : Window
    {
        private readonly UserManager _userManager;
        private readonly List<PortalUser> _batch = new();
        public string PortalUrl { get; set; } = "http://192.168.137.1:8080";

        public BulkTicketsWindow(UserManager userManager)
        {
            _userManager = userManager;
            InitializeComponent();
        }

        private void BtnBulkGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtBulkCount.Text.Trim(), out int count) || count < 1 || count > 100)
            {
                TxtBulkInfo.Text = "Adet 1-100 arası olmalı.";
                return;
            }

            _batch.Clear();
            for (int i = 0; i < count; i++)
            {
                try { _batch.Add(_userManager.GenerateRandomUser()); }
                catch { break; }
            }

            RenderDoc();
            TxtBulkInfo.Text = $"{_batch.Count} bilet üretildi.";
        }

        private void RenderDoc()
        {
            TicketsDoc.Blocks.Clear();

            var title = new Paragraph(new Run("Wi-Fi Giriş Biletleri"))
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            TicketsDoc.Blocks.Add(title);

            var sub = new Paragraph(new Run($"Giriş sayfası: {PortalUrl}  •  Her bilet 1 cihaz içindir"))
            {
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(85, 85, 85)),
                Margin = new Thickness(0, 0, 0, 16)
            };
            TicketsDoc.Blocks.Add(sub);

            int no = 1;
            foreach (var u in _batch)
            {
                var p = new Paragraph
                {
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 0, 10),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 180, 180)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 0, 0, 6)
                };
                p.Inlines.Add(new Run($"#{no}  ") { FontWeight = FontWeights.Bold });
                p.Inlines.Add(new Run($"Kullanıcı: {u.Username}   "));
                p.Inlines.Add(new Run($"Şifre: {u.Password}") { FontWeight = FontWeights.Bold });
                TicketsDoc.Blocks.Add(p);
                no++;
            }
        }

        private void BtnPrint_Click(object sender, RoutedEventArgs e)
        {
            if (_batch.Count == 0)
            {
                TxtBulkInfo.Text = "Önce bilet üretin.";
                return;
            }
            try
            {
                var dlg = new System.Windows.Controls.PrintDialog();
                if (dlg.ShowDialog() == true)
                {
                    dlg.PrintDocument(((IDocumentPaginatorSource)TicketsDoc).DocumentPaginator, "Wi-Fi Biletleri");
                    TxtBulkInfo.Text = "Yazdırmaya gönderildi.";
                }
            }
            catch (Exception ex)
            {
                TxtBulkInfo.Text = $"Yazdırılamadı: {ex.Message}";
            }
        }

        private void BtnCopyAll_Click(object sender, RoutedEventArgs e)
        {
            if (_batch.Count == 0) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Giriş sayfası: {PortalUrl}");
                foreach (var u in _batch)
                    sb.AppendLine($"Kullanıcı: {u.Username}  Şifre: {u.Password}");
                System.Windows.Clipboard.SetText(sb.ToString());
                TxtBulkInfo.Text = "Panoya kopyalandı.";
            }
            catch { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
