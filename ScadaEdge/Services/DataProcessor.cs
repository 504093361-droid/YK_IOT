using Contracts.Models;
using System;
using System.Collections.Generic;
using System.Text;
namespace ScadaEdge.Services;

public static class DataProcessor
{
    /// <summary>
    /// 对原始设备数据进行清洗、转换，并输出标准化遥测数据
    /// </summary>
    public static TelemetryMessage CleanseAndConvert(RawDeviceMessage raw)
    {
        var quality = "Good";

        if (raw.Temperature < -50 || raw.Temperature > 200)
        {
            quality = "Bad";
        }

        return new TelemetryMessage
        {
            Namespace = $"uns/site1/line1/{raw.DeviceId}/temperature",
            DeviceId = raw.DeviceId,
            Timestamp = raw.Timestamp,
            Value = raw.Temperature,
            Unit = "Celsius",
            Quality = quality
        };
    }

    /// <summary>
    /// 根据遥测数据尝试生成事件
    /// 当前先实现：温度超限事件
    /// </summary>
    public static EventMessage? TryCreateTelemetryEvent(RawDeviceMessage raw, TelemetryMessage telemetry)
    {
        // 示例规则 1：温度超限
        if (telemetry.Value > 85)
        {
            return new EventMessage
            {
                EventId = Guid.NewGuid().ToString("N"),
                Namespace = $"event/site1/line1/{raw.DeviceId}",
                DeviceId = raw.DeviceId,
                EventType = EventType.ThresholdExceeded,
                Severity = EventSeverity.Warning,
                EventName = "TemperatureHigh",
                Message = $"设备 {raw.DeviceId} 温度超限，当前值：{telemetry.Value} {telemetry.Unit}",
                SourceSystem = "ScadaEdge",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        // 示例规则 2：原始值严重异常
        if (telemetry.Quality == "Bad")
        {
            return new EventMessage
            {
                EventId = Guid.NewGuid().ToString("N"),
                Namespace = $"event/site1/line1/{raw.DeviceId}",
                DeviceId = raw.DeviceId,
                EventType = EventType.Alarm,
                Severity = EventSeverity.Error,
                EventName = "TelemetryInvalid",
                Message = $"设备 {raw.DeviceId} 采集到异常数据，Temperature={raw.Temperature}",
                SourceSystem = "ScadaEdge",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        return null;
    }
}
