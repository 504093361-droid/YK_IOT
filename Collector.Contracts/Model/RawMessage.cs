using System;
using System.Collections.Generic;
using System.Text;

namespace Collector.Contracts.Model
{
    public class RawMessage
    {
        public string DeviceId { get; set; } = "";
        public string PointId { get; set; } = "";
        public string Address { get; set; } = "";
        public object? Value { get; set; }
        public string DataType { get; set; } = "";
        public DateTime CollectTime { get; set; }
        public bool IsSuccess { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
