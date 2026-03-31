using Collector.Contracts;
using Collector.Contracts.Enums;
using Collector.Contracts.Model;
using System;
using System.Collections.Generic;
using System.Text;

namespace Collector.Edge.Engine
{
    public static class ModbusBatchOptimizer
    {
        /// <summary>
        /// 🟢 自动将散乱的 Modbus 点位，合并成最优的读取块 (Gap < 5 自动合并)
        /// </summary>
        public static List<BatchReadGroup> Optimize(IEnumerable<PointConfig> points, int maxGapWords = 5)
        {
            var groups = new List<BatchReadGroup>();

            // 过滤并按地址数字排序 (假设地址都是类似 "40001", "40010" 纯数字结尾)
            var validPoints = points
                // 🚨 🟢 核心修复：剔除 Bool 类型！把开关量赶回单点专属通道 (ReadBool)！
                .Where(p => p.DataType != DataTypeEnum.Bool)
                .Select(p => new { Config = p, AddressNum = ExtractModbusAddress(p.Address) })
                .Where(x => x.AddressNum > 0)
                .OrderBy(x => x.AddressNum)
                .ToList();

            if (validPoints.Count == 0) return groups;

            BatchReadGroup currentGroup = null;
            int currentGroupEndNum = 0;

            foreach (var item in validPoints)
            {
                int pointLengthWords = GetModbusWordLength(item.Config); // 这个点位占几个 Word

                // 如果是第一个点，或者与上一个块的距离超过了设定的缝隙 (MaxGap)，则新开一个包裹
                if (currentGroup == null || (item.AddressNum - currentGroupEndNum) > maxGapWords)
                {
                    currentGroup = new BatchReadGroup
                    {
                        StartAddress = item.Config.Address // 以当前点位地址作为块的起始
                    };
                    groups.Add(currentGroup);
                }

                // 计算当前点位在【当前包裹】中的字节偏移量
                // 公式：(当前地址 - 包裹起始地址) * 2个字节
                int startAddressNum = ExtractModbusAddress(currentGroup.StartAddress);
                int byteOffset = (item.AddressNum - startAddressNum) * 2;

                currentGroup.Points.Add(new BatchPointMeta
                {
                    Point = item.Config,
                    ByteOffset = byteOffset,
                    ByteLength = pointLengthWords * 2
                });

                // 更新当前包裹的末尾边界
                int thisPointEndNum = item.AddressNum + pointLengthWords;
                if (thisPointEndNum > currentGroupEndNum)
                {
                    currentGroupEndNum = thisPointEndNum;
                    currentGroup.TotalLength = (ushort)(currentGroupEndNum - startAddressNum);
                }
            }

            return groups;
        }

        // 提取地址数字 (比如 "40001" -> 40001)
        private static int ExtractModbusAddress(string address)
        {
            // 剔除可能的协议前缀（比如有时候配置成 "s=2;x=4;40001"）
            string cleanAddr = address.Split(';').Last();
            var digits = new string(cleanAddr.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out int result) ? result : 0;
        }

        // 计算不同数据类型在 Modbus 中占用多少个 Word (1 Word = 2 Bytes)
        private static int GetModbusWordLength(PointConfig point)
        {
            return point.DataType switch
            {
                DataTypeEnum.Bool => 1, // Modbus 线圈也是按位/字走的，简化处理占1字
                DataTypeEnum.Short => 1,
                DataTypeEnum.Int => 2,
                DataTypeEnum.Float => 2,
                DataTypeEnum.String => (point.Length + 1) / 2, // 比如 10 个字节，就是 5 个 Word
                _ => 1
            };
        }
    }
}
