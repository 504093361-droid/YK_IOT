namespace Collector.Contracts.Model
{
    // 🟢 专门用于在 MQTT 上传输设备状态的 DTO 契约
    public class DeviceStatusMessage
    {
        public string DeviceId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int StatusCode { get; set; }
    }
}