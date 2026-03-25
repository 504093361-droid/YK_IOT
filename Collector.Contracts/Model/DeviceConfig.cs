using Collector.Contracts.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace Collector.Contracts
{
    public partial class DeviceConfig : ObservableObject
    {

        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";

        [ObservableProperty]
        public ProtocolTypeEnum protocolType = ProtocolTypeEnum.MQTT;
        public string IpAddress { get; set; } = "";
        public int Port { get; set; }
        public int ScanIntervalMs { get; set; } = 1000;


        [ObservableProperty]
        private ObservableCollection<PointConfig> points = new();
    }
}
