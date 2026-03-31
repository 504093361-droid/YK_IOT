using Collector.Contracts;
using Collector.Contracts.Enums;
using Collector.Contracts.Model;
using HslCommunication;
using NCalc;
using System;

namespace Collector.Edge.Processing
{
    public interface IDataProcessor
    {
        StandardPointData Process(DeviceConfig device, PointConfig point, OperateResult<object> rawResult);
    }

    public class DataProcessor : IDataProcessor
    {
        public StandardPointData Process(DeviceConfig device, PointConfig point, OperateResult<object> rawResult)
        {
            var msg = new StandardPointData
            {
                DeviceId = device.DeviceId,
                PointId = point.PointId,
                CollectTime = DateTime.Now,
                IsSuccess = rawResult.IsSuccess,
                ErrorMessage = rawResult.Message ?? string.Empty
            };

            // 1. 空值/非法值处理
            if (!rawResult.IsSuccess || rawResult.Content == null)
            {
                msg.RawValue = null;
                msg.ProcessedValue = "NaN / Error"; // 兜底非法状态
                return msg;
            }

            // 2. 提取原始值
            object rawValue = rawResult.Content;
            msg.RawValue = rawValue;

            // 3. 执行清洗与转换
            try
            {
                msg.ProcessedValue = ExecuteTransform(point, rawValue);
            }
            catch (Exception ex)
            {
                msg.IsSuccess = false;
                msg.ErrorMessage = $"数据转换规则异常: {ex.Message}";
                msg.ProcessedValue = "Transform_Failed";
            }

            // 🟢 4. 终极质检：死区过滤与差异比对
            msg.HasChanged = CheckIfValueChanged(point, msg.ProcessedValue);

            // 如果确认发生了有效跳变，或者发生了异常，更新内存里的“上一次发布值”
            if (msg.HasChanged || !msg.IsSuccess)
            {
                point.LastPublishedValue = msg.ProcessedValue;
            }

            return msg;
        }

        // 🟢 新增的死区比对裁判官
        private bool CheckIfValueChanged(PointConfig point, object? newValue)
        {
            // 如果任何一个是 null（比如第一次读取，或者断线了），强制认为发生了跳变
            if (newValue == null || point.LastPublishedValue == null) return true;

            // 1. 如果是数值类型，进行【死区比对】
            if (IsNumericType(newValue) && IsNumericType(point.LastPublishedValue))
            {
                double current = Convert.ToDouble(newValue);
                double last = Convert.ToDouble(point.LastPublishedValue);

                // 只有当差值的绝对值 > 设定的死区阈值时，才算发生了跳变
                return Math.Abs(current - last) > point.Deadband;
            }

            // 2. 如果是布尔或字符串等非数值类型，直接进行【全等比对】
            return !newValue.Equals(point.LastPublishedValue);
        }

        // 🟢 核心转换引擎
        private object ExecuteTransform(PointConfig point, object rawValue)
        {
            // A. 字符串类型，直接清理抛出，不参与数学运算
            if (point.DataType == DataTypeEnum.String)
            {
                return rawValue.ToString()?.Trim() ?? string.Empty;
            }

            // B. 数值类型的运算逻辑
            if (IsNumericType(rawValue))
            {
                double numericValue = Convert.ToDouble(rawValue);

                // 🟢 1. 优先判定：是否有自定义数学表达式？
                if (!string.IsNullOrWhiteSpace(point.Expression))
                {
                    // 实例化 NCalc 解析器，传入用户写的公式
                    Expression e = new Expression(point.Expression);

                    // 绑定变量：把公式里的 'x' 替换成我们实际读到的 numericValue
                    e.Parameters["x"] = numericValue;
                    // 为了兼顾用户的不同习惯，也可以把 'raw' 绑定上
                    e.Parameters["raw"] = numericValue;

                    // 执行运算
                    object result = e.Evaluate();
                    return Math.Round(Convert.ToDouble(result), 3);
                }

                // 🟢 2. 降级判定：如果没有公式，则使用基础的 y = kx + b
                if (point.Multiplier != 1.0 || point.Offset != 0.0)
                {
                    double calculated = (numericValue * point.Multiplier) + point.Offset;
                    return Math.Round(calculated, 3);
                }

                // 🟢 3. 什么都没配，原样返回数值
                return numericValue;
            }

            // C. 布尔类型或其他的状态透出
            return rawValue;
        }

        private bool IsNumericType(object obj)
        {
            return obj is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
        }
    }
}