using Collector.Contracts.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Serialization;

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

        // 在 DeviceConfig.cs 中新增：
        [ObservableProperty]
        private string _workshop = "V车间"; // 默认分配给一车间

        [ObservableProperty]
        private ObservableCollection<PointConfig> points = new();

        // =========================================
        // 🔧 Modbus 专属高级配置
        // =========================================
        // 🟢 新增：Modbus 大小端字节序配置 (默认 CDAB 是大多数国内设备的标准)
        [ObservableProperty]
        private DataFormatEnum _dataFormat = DataFormatEnum.CDAB;

        // 🟢 新增：首地址是否从 0 开始 (默认 true，也是标准做法)
        [ObservableProperty]
        private bool _isAddressStartWithZero = true;

        /// <summary>
        /// 站号 / 从站地址 (Slave ID)
        /// 默认通常为 1。在使用串口服务器透传多个子设备时极其重要！
        /// </summary>
        public byte Station { get; set; } = 1;


        // =========================================
        // 🛡️ OPC UA 专属高级配置
        // =========================================
        /// <summary>
        /// OPC UA 端点路由后缀 (例如 /UA/Server，留空则代表根路径)
        /// </summary>
        [ObservableProperty]
        private string _opcEndpointPath = "";

        /// <summary>
        /// OPC UA 认证用户名 (留空代表匿名登录)
        /// </summary>
        [ObservableProperty]
        private string _opcUsername = "";

        /// <summary>
        /// OPC UA 认证密码
        /// </summary>
        [ObservableProperty]
        private string _opcPassword = "";


        // =========================================
        // 👇 观察窗专属字段：标识 Edge 端的 Worker 状态 👇
        // =========================================

        // 例如："在线/采集中", "离线/断开", "异常停止"
        [property: JsonIgnore]
        [ObservableProperty]
        private string _workerStatus = "未启动";

        [property: JsonIgnore]
        // 🟢 新增：专门给 UI 用的红绿灯状态 (1=绿灯在线, 0=灰灯未启动, -1=红灯报错)
        [ObservableProperty]
        private int _statusCode = -1;
    }
}