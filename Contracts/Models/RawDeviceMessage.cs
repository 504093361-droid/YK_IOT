using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Models
{
    public class RawDeviceMessage
    {
        public string DeviceId { get; set; }  // 设备ID
        public long Timestamp { get; set; }   // 时间戳（毫秒级）
        public double Temperature { get; set; }  // 温度原始数据
        public double Pressure { get; set; }     // 压力原始数据
    }
}
