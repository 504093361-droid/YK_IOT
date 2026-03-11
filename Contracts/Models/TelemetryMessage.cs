using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Models
{
    public class TelemetryMessage
    {
        public string Namespace { get; set; }  // 数据的命名空间，例如 "uns/site1/line1/mixer01/temperature"
        public string DeviceId { get; set; }   // 设备ID
        public long Timestamp { get; set; }    // 时间戳
        public double Value { get; set; }      // 清洗后的值（例如转换为摄氏度）
        public string Unit { get; set; }       // 单位，例如 "Celsius"
        public string Quality { get; set; }    // 数据质量（Good/Bad）
    }
}
