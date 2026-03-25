using Collector.Contracts;
using Collector.Contracts.Enums;
using Collector.Contracts.Model;
using Collector.Edge.Processing;
using Collector.Edge.Publishing;
using HslCommunication;
using HslCommunication.Core.Device;
using HslCommunication.Core.Net; // DeviceTcpNet 所在的命名空间

using HslCommunication.ModBus;
using HslCommunication.Profinet.Siemens;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Collector.Edge.Engine
{
    public class DeviceCollectWorker
    {
        private readonly DeviceConfig _device;
        private readonly IDataProcessor _processor;
        private readonly IMqttPublisher _publisher;
        private readonly ILogger _logger;

        // 【绝对严谨】：严格使用你补充的真实基类 DeviceTcpNet
        private DeviceTcpNet _plcClient;
        private CancellationTokenSource _cts;

        public DeviceCollectWorker(
            DeviceConfig device,
            IDataProcessor processor,
            IMqttPublisher publisher,
            ILogger logger)
        {
            _device = device;
            _processor = processor;
            _publisher = publisher;
            _logger = logger;
        }

        public void Start()
        {
            _logger.LogInformation("准备启动设备 [{DeviceName}] 的采集 Worker...", _device.DeviceName);

            _plcClient = CreatePlcClient(_device);
            _cts = new CancellationTokenSource();

            // 启动后台轮询任务
            Task.Run(() => PollingLoopAsync(_cts.Token), _cts.Token);
        }

        public void Stop()
        {
            _logger.LogInformation("正在停止设备 [{DeviceName}] 的采集 Worker...", _device.DeviceName);
            _cts?.Cancel();
            _plcClient?.ConnectClose(); // 调用基类真实的断开连接方法
        }

        private async Task PollingLoopAsync(CancellationToken token)
        {
            // 首次主动尝试连接，获取底层真实的 OperateResult
            OperateResult connectResult = await _plcClient.ConnectServerAsync();
            if (!connectResult.IsSuccess)
            {
                _logger.LogWarning("设备 [{DeviceName}] 初始连接失败，将进入轮询重试！原因: {Msg}", _device.DeviceName, connectResult.Message);
            }

            while (!token.IsCancellationRequested)
            {
                foreach (var point in _device.Points)
                {
                    if (token.IsCancellationRequested) break;

                    // 1. 读取 PLC 数据 (底层异常全部封装在 OperateResult 内)
                    OperateResult<object> readResult = ReadPointValue(point);

                    // 2. 交给 Processing 层清洗封装
                    RawMessage rawMessage = _processor.ProcessRawData(_device, point, readResult);

                    // 3. 交给 Publishing 层发往 MQTT
                    await _publisher.PublishRawDataAsync(rawMessage);
                }

                // 严格遵守扫描周期
                await Task.Delay(_device.ScanIntervalMs, token);
            }
        }

        /// <summary>
        /// 核心读取：严格基于 HSL 的知识库规范进行泛型结果转换
        /// </summary>
        private OperateResult<object> ReadPointValue(PointConfig point)
        {
            switch (point.DataType)
            {
                case DataTypeEnum.Int:
                    OperateResult<int> resInt32 = _plcClient.ReadInt32(point.Address);
                    // 严格遵守先校验 IsSuccess 的铁律，并使用静态工厂方法透传错误
                    if (!resInt32.IsSuccess) return OperateResult.CreateFailedResult<object>(resInt32);
                    return OperateResult.CreateSuccessResult<object>(resInt32.Content);

                case DataTypeEnum.Float:
                    OperateResult<float> resFloat = _plcClient.ReadFloat(point.Address);
                    if (!resFloat.IsSuccess) return OperateResult.CreateFailedResult<object>(resFloat);
                    return OperateResult.CreateSuccessResult<object>(resFloat.Content);

                case DataTypeEnum.Bool:
                    OperateResult<bool> resBool = _plcClient.ReadBool(point.Address);
                    if (!resBool.IsSuccess) return OperateResult.CreateFailedResult<object>(resBool);
                    return OperateResult.CreateSuccessResult<object>(resBool.Content);

                case DataTypeEnum.Short:

                    OperateResult<short> resShort = _plcClient.ReadInt16(point.Address);
                    if (!resShort.IsSuccess) return OperateResult.CreateFailedResult<object>(resShort);
                    return OperateResult.CreateSuccessResult<object>(resShort.Content);

                default:
                    return new OperateResult<object>($"Worker 不支持的点位数据类型: {point.DataType}");
            }
        }

        /// <summary>
        /// 实例化真实存在的协议客户端，它们都继承自 DeviceTcpNet
        /// </summary>
        private DeviceTcpNet CreatePlcClient(DeviceConfig device)
        {
            return device.ProtocolType switch
            {
                ProtocolTypeEnum.ModbusTCP => new ModbusTcpNet(device.IpAddress, device.Port),
                ProtocolTypeEnum.S71200 => new SiemensS7Net(SiemensPLCS.S1200, device.IpAddress) { Port = device.Port },
                _ => throw new NotSupportedException($"不支持的协议类型: {device.ProtocolType}")
            };
        }
    }
}