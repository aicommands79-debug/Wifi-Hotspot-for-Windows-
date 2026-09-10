using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Windows;

namespace Win11HotspotManager;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public App()
    {
        // Tek örnek: eski kopyalar varsa kapat (güncelleme sonrası çift çalışma ve kilit sorunu bitirir).
        // Yeni kopya admin yetkisiyle çalıştığı için eski admin kopyaları kapatabilir.
        KillOlderInstances();
        CleanupOldReleases();

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                System.IO.File.WriteAllText("crash_appdomain.log", e.ExceptionObject.ToString());
            }
            catch { }
        };

        DispatcherUnhandledException += (s, e) =>
        {
            try
            {
                System.IO.File.WriteAllText("crash_dispatcher.log", e.Exception.ToString());
            }
            catch { }
        };
    }

    private static void KillOlderInstances()
    {
        try
        {
            int self = Environment.ProcessId;
            var others = new System.Collections.Generic.List<Process>();
            foreach (var p in Process.GetProcessesByName("Win11HotspotManager"))
            {
                if (p.Id == self) continue;
                others.Add(p);
            }
            if (others.Count == 0) return;

            foreach (var p in others)
            {
                try { p.Kill(); } catch { }
            }

            // Kilitlerin bırakılması için kısa bekleme (en fazla ~5 sn)
            var sw = Stopwatch.StartNew();
            bool allGone;
            do
            {
                allGone = true;
                foreach (var p in others)
                {
                    try
                    {
                        if (!p.HasExited) { allGone = false; break; }
                    }
                    catch { }
                }
                if (!allGone) System.Threading.Thread.Sleep(250);
            } while (!allGone && sw.Elapsed.TotalSeconds < 5);

            foreach (var p in others)
            {
                try { p.Dispose(); } catch { }
            }
        }
        catch { }
    }

    private static void CleanupOldReleases()
    {
        try
        {
            string ownDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            string? parent = System.IO.Path.GetDirectoryName(ownDir);
            if (parent == null || System.IO.Path.GetFileName(parent) != "Yayin") return;

            foreach (var dir in System.IO.Directory.GetDirectories(parent))
            {
                if (string.Equals(dir.TrimEnd('\\', '/'), ownDir, StringComparison.OrdinalIgnoreCase)) continue;
                try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
            }
        }
        catch { }
    }
}
