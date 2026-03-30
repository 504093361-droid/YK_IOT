using Collector.Contracts;
using Collector.Contracts.Enums;
using Collector.Contracts.Model;
using Collector.Contracts.Topics;
using Collector.Edge.Processing;
using Collector.Edge.Publishing;
using HslCommunication;
using HslCommunication.Core.Device;
using HslCommunication.Core.Net; // DeviceTcpNet 所在的命名空间

using HslCommunication.ModBus;
using HslCommunication.Profinet.Siemens;
using Microsoft.Extensions.Logging;
using System;
using System.Text;
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
        // 在类顶部增加变量：
        private Task _pollingTask;
        public void Start()
        {
            _logger.LogInformation("准备启动设备 [{DeviceName}] 的采集 Worker...", _device.DeviceName);

            _plcClient = CreatePlcClient(_device);
            _cts = new CancellationTokenSource();

            // 启动后台轮询任务
          //  Task.Run(() => PollingLoopAsync(_cts.Token), _cts.Token);

            // 🟢 核心：把这个跑在后台的 Task 存起来，方便以后等待它结束
            _pollingTask = Task.Run(() => PollingLoopAsync(_cts.Token), _cts.Token);
        }

        public async Task StopAsync()
        {
            _logger.LogInformation("正在停止设备 [{DeviceName}] 的采集 Worker...", _device.DeviceName);
            _cts?.Cancel();
            _plcClient?.ConnectClose();

            // 🟢 核心：等待后台线程把最后的遗言（状态0）发完，真正退出 while 循环！
            if (_pollingTask != null)
            {
                try { await _pollingTask; } catch { /* 忽略取消引发的异常 */ }
            }
        }

        private async Task PollingLoopAsync(CancellationToken token)
        {
            // 🟢 1. 记录当前的状态码，用来做“跳变检测”（防抖，避免疯狂发重复状态）
            int currentStatusCode = 0;

            // 首次主动尝试连接
            OperateResult connectResult = await _plcClient.ConnectServerAsync();
            if (connectResult.IsSuccess)
            {
                currentStatusCode = 1;
                await _publisher.PublishDeviceStatusAsync(_device.DeviceId, "在线/采集中", 1);
            }
            else
            {
                currentStatusCode = -1;
                await _publisher.PublishDeviceStatusAsync(_device.DeviceId, $"连接失败: {connectResult.Message}", -1);
            }

            // 进入死循环轮询
            while (!token.IsCancellationRequested)
            {
                bool allPointsFailed = true; // 悲观假设：默认全军覆没
                string lastError = "";

                // 遍历读取每一个点位
                foreach (var point in _device.Points)
                {
                    if (token.IsCancellationRequested) break;

                    OperateResult<object> readResult = ReadPointValue(point);

                    if (readResult.IsSuccess)
                    {
                        // 🟢 只要有一个点位能读通，说明物理网线和设备芯片是活着的
                        allPointsFailed = false;
                    }
                    else
                    {
                        lastError = readResult.Message; // 记录最后一次的报错原因
                    }

                    // 发送点位数据
                    //   RawMessage rawMessage = _processor.ProcessRawData(_device, point, readResult);
                    StandardPointData processedData = _processor.Process(_device, point, readResult);
                    //await _publisher.PublishRawDataAsync(rawMessage);
                    await _publisher.PublishPointDataAsync(processedData);

                }

                // 🟢 2. 核心诊断：这一轮扫完后，评估设备的宏观健康状态
                if (_device.Points.Count > 0 && !token.IsCancellationRequested)
                {
                    // 如果所有点都挂了，判定为断线(-1)；否则判定为正常(1)
                    int newStatusCode = allPointsFailed ? -1 : 1;

                    // 🟢 3. 边缘端节流：只有当状态发生“跳变”时，才向 UI 广播新的状态！
                    // 比如：在线 -> 突然拔网线 -> 发送红灯
                    //      一直断网 -> 不发
                    //      插上网线 -> 恢复绿灯 -> 发送绿灯
                    if (newStatusCode != currentStatusCode)
                    {
                        currentStatusCode = newStatusCode;
                        string statusText = currentStatusCode == 1 ? "在线/采集中" : $"连接异常/断线: {lastError}";

                        await _publisher.PublishDeviceStatusAsync(_device.DeviceId, statusText, currentStatusCode);
                        _logger.LogWarning("设备 [{Device}] 状态发生跳变 -> {Status}", _device.DeviceName, statusText);
                    }
                }

                // 严格遵守扫描周期，并捕获取消异常
                try
                {
                    await Task.Delay(_device.ScanIntervalMs, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            // 3. 循环被取消（如重新下发配置或程序退出时），通知 UI 变灰
            await _publisher.PublishDeviceStatusAsync(_device.DeviceId, "已停止", 0);
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
                // UI 端直接配置 Address="M100.1" 或 Address="DB1.0.5"，HSL 会自动按位解析！
                case DataTypeEnum.Bool:
                    OperateResult<bool> resBool = _plcClient.ReadBool(point.Address);
                    if (!resBool.IsSuccess) return OperateResult.CreateFailedResult<object>(resBool);
                    return OperateResult.CreateSuccessResult<object>(resBool.Content);

                case DataTypeEnum.Short:

                    OperateResult<short> resShort = _plcClient.ReadInt16(point.Address);
                    if (!resShort.IsSuccess) return OperateResult.CreateFailedResult<object>(resShort);
                    return OperateResult.CreateSuccessResult<object>(resShort.Content);

                // 🟢 新增：字符串读取，必须传入 PointConfig 里的 Length 字段！
                case DataTypeEnum.String:
                    // HSL 读取字符串必须知道读几个字节 (point.Length)
                    OperateResult<string> resString = _plcClient.ReadString(point.Address, point.Length,Encoding.UTF8);
                    if (!resString.IsSuccess) return OperateResult.CreateFailedResult<object>(resString);
                    // 🟢 核心防御：PLC 里的字符串通常是定长分配的（比如留了 10 个字节，但只写了 "操" 占 3 个字节）
                    // 剩下的 7 个字节全是 \0 (空字符)。如果不 Trim 掉，传给 UI 会引发不可预知的显示异常或 JSON 解析截断！
                    string cleanString = resString.Content?.TrimEnd('\0');
                    return OperateResult.CreateSuccessResult<object>(cleanString);


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