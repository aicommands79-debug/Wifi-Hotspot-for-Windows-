using System;
using System.Diagnostics;

namespace Win11HotspotManager.Services
{
    /// <summary>
    /// Gerçek cihazlarda otomatik portal açılması için Windows Güvenlik Duvarı
    /// gelen kuralları gerekir (TCP 80/8080 + UDP 53). Admin yetkisiyle sessizce ekler.
    /// </summary>
    public static class FirewallHelper
    {
        public static void EnsurePortalRules()
        {
            try
            {
                Allow("Win11Hotspot_HTTP80", "TCP", "80");
                Allow("Win11Hotspot_HTTP8080", "TCP", "8080");
                Allow("Win11Hotspot_DNS53_UDP", "UDP", "53");
                Allow("Win11Hotspot_DNS53_TCP", "TCP", "53");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Firewall rule error: {ex.Message}");
            }
        }

        private static void Allow(string name, string protocol, string port)
        {
            try
            {
                // Varsa tekrar eklemez, yoksa ekler. Çıktıyı gizle.
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall show rule name=\"{name}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return;
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    if (output.Contains(name, StringComparison.OrdinalIgnoreCase))
                        return; // Kural zaten var
                }

                var addPsi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol={protocol} localport={port} profile=any",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p2 = Process.Start(addPsi))
                {
                    p2?.WaitForExit(8000);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Firewall allow {name} error: {ex.Message}");
            }
        }
    }
}
