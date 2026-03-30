using System;
using System.Collections.Generic;
using System.Text;

namespace Collector.Contracts.Model
{
    public class StandardPointData
    {
        public string DeviceId { get; set; } = string.Empty;
        public string PointId { get; set; } = string.Empty;

        public object? RawValue { get; set; }       // 原始生肉
        public object? ProcessedValue { get; set; } // 熟肉 (可以直接给业务看)

        public bool IsSuccess { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public DateTime CollectTime { get; set; }
    }
}
