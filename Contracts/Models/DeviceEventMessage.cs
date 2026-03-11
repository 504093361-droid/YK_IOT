using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Models
{
    public class DeviceEventMessage
    {
        public string DeviceId { get; set; }   // 设备ID
        public string EventType { get; set; }   // 事件类型（如：Alarm, Shutdown）
        public string Message { get; set; }     // 事件详细描述
        public long Timestamp { get; set; }     // 时间戳
    }
}
