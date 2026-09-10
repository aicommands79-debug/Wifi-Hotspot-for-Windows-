using System.Configuration;
using System.Data;
using System.Windows;

namespace Win11HotspotManager;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public App()
    {
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
}

