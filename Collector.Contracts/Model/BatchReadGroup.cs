using System;
using System.Collections.Generic;
using System.Text;

namespace Collector.Contracts.Model
{
    // 🟢 连续地址读取的“批处理任务包”
    public class BatchReadGroup
    {
        public string StartAddress { get; set; } = string.Empty; // 包围盒的起始地址 (如 "40001")
        public ushort TotalLength { get; set; } // 读取总长度 (Modbus是字数，西门子是字节数)

        // 这个包里包含的所有点位，以及它们在 byte[] 中的偏移量
        public List<BatchPointMeta> Points { get; set; } = new();
    }

    public class BatchPointMeta
    {
        public PointConfig Point { get; set; } = null!;
        public int ByteOffset { get; set; } // 在返回的 byte[] 中的起始位置
        public int ByteLength { get; set; } // 占用字节数
    }
}
