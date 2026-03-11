using Contracts.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace ScadaEdge.Services
{
    public static class DataProcessor
    {
        /// <summary>
        /// 对原始设备数据进行清洗、转换，并输出标准化遥测数据
        /// </summary>
        /// <param name="raw">原始设备数据</param>
        /// <returns>标准化后的遥测数据</returns>
        public static TelemetryMessage CleanseAndConvert(RawDeviceMessage raw)
        {
            // 1. 默认数据质量为 Good
            var quality = "Good";

            // 2. 进行最简单的质量判断
            // 这里只是演示：温度超出合理范围则视为 Bad
            // 未来你可以扩展为：
            // - 通讯异常
            // - 设备离线
            // - 冻结值
            // - 无效寄存器值
            if (raw.Temperature < -50 || raw.Temperature > 200)
            {
                quality = "Bad";
            }

            // 3. 数据转换
            // 目前假设设备上传的已经是工程值温度
            // 如果以后是 PLC 原始寄存器值，这里就可以加比例换算、单位转换等逻辑
            var value = raw.Temperature;

            // 4. 生成标准化的 TelemetryMessage
            // Namespace 相当于 UNS 的命名空间路径
            // 未来你可以改成更完整的层级，例如：
            // enterprise/site/area/line/equipment/tag
            return new TelemetryMessage
            {
                Namespace = $"uns/site1/line1/{raw.DeviceId}/temperature",
                DeviceId = raw.DeviceId,
                Timestamp = raw.Timestamp,
                Value = value,
                Unit = "Celsius",
                Quality = quality
            };
        }
    }
    }
