using Collector.Contracts;
using Collector.Contracts.Model;
using HslCommunication;
using System;

namespace Collector.Edge.Processing
{
    public interface IDataProcessor
    {
        RawMessage ProcessRawData(DeviceConfig device, PointConfig point, OperateResult<object> readResult);
    }

    public class DataProcessor : IDataProcessor
    {
        public RawMessage ProcessRawData(DeviceConfig device, PointConfig point, OperateResult<object> readResult)
        {
            return new RawMessage
            {
                DeviceId = device.DeviceId,
                PointId = point.PointId,
                Address = point.Address,
                DataType = point.DataType.ToString(),
                CollectTime = DateTime.Now,

                // 完全基于 HSL 的设计机制提取状态
                IsSuccess = readResult.IsSuccess,
                Value = readResult.IsSuccess ? readResult.Content : null,
                ErrorMessage = readResult.IsSuccess ? string.Empty : readResult.Message
            };
        }
    }
}