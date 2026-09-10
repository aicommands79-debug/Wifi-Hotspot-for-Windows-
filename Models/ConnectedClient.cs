using System;

namespace Win11HotspotManager.Models
{
    public class ConnectedClient
    {
        public string DeviceName { get; set; } = "Bilinmeyen Cihaz";
        public string IpAddress { get; set; } = "-";
        public string MacAddress { get; set; } = "-";
        public DateTime ConnectedTime { get; set; } = DateTime.Now;
        public string Status { get; set; } = "Bağlı";
    }
}
