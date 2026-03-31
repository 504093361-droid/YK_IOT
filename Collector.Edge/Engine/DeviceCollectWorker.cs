using Collector.Contracts;
using Collector.Contracts.Enums;
using Collector.Contracts.Model;
using Collector.Contracts.Topics;
using Collector.Edge.Processing;
using Collector.Edge.Publishing;
using HslCommunication;
using HslCommunication.Core.Device;
using HslCommunication.Core.Net;
using HslCommunication.ModBus;
using HslCommunication.Profinet.Siemens;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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

        // 【绝对严谨】：严格使用真实基类 DeviceTcpNet
        private DeviceTcpNet _plcClient;
        private CancellationTokenSource _cts;

        private readonly SemaphoreSlim _concurrencyLock; // 🟢 接收全局锁

        private List<BatchReadGroup> _batchGroups = new(); // 🟢 合包策略缓存
        // 🟢 新增：专门用来存放那些不能合包的点位（如 Bool、String、或非 Modbus 设备）
        private List<PointConfig> _unbatchedPoints = new();
        private Task _pollingTask;

        public DeviceCollectWorker(
            DeviceConfig device,
            IDataProcessor processor,
            IMqttPublisher publisher,
            ILogger logger,
            SemaphoreSlim concurrencyLock)
        {
            _device = device;
            _processor = processor;
            _publisher = publisher;
            _logger = logger;
            _concurrencyLock = concurrencyLock;
        }

        public void Start()
        {
            _logger.LogInformation("准备启动设备 [{DeviceName}] 的采集 Worker...", _device.DeviceName);

            _plcClient = CreatePlcClient(_device);
            _cts = new CancellationTokenSource();

            if (_device.ProtocolType == ProtocolTypeEnum.ModbusTCP)
            {
                _batchGroups = ModbusBatchOptimizer.Optimize(_device.Points);

                // 🟢 核心修复：把那些被合包算法踢出来的点位（Bool/String），挑出来放进单点名单里
                var batchedPointIds = new HashSet<string>();
                foreach (var g in _batchGroups)
                    foreach (var p in g.Points)
                        batchedPointIds.Add(p.Point.PointId);

                foreach (var p in _device.Points)
                {
                    if (!batchedPointIds.Contains(p.PointId))
                        _unbatchedPoints.Add(p);
                }

                _logger.LogInformation("🚀 极速引擎挂载：[{Device}] 共 {pCount} 个点位，{gCount} 组进入批量通道，{uCount} 个进入单点通道！",
                    _device.DeviceName, _device.Points.Count, _batchGroups.Count, _unbatchedPoints.Count);
            }
            else
            {
                // 如果是西门子等没有合包算法的设备，所有点位全部进入单点通道
                _unbatchedPoints.AddRange(_device.Points);
            }

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
            // 1. 记录当前的状态码，用来做“跳变检测”
            int currentStatusCode = 0;

            // 记录上一次发送状态的时间
            DateTime lastStatusPublishTime = DateTime.MinValue;

            // 极简思路：设置一个“前 3 次免检”的计数器
            int startupBypassCount = 3;

            // 连续失败计数器，用于触发熔断
            int consecutiveFailures = 0;

            // 首次主动尝试连接
            OperateResult connectResult = await _plcClient.ConnectServerAsync();
            if (connectResult.IsSuccess)
            {
                currentStatusCode = 1;
                await _publisher.PublishDeviceStatusAsync(_device.DeviceId, "在线/采集中", 1);
                lastStatusPublishTime = DateTime.Now;
            }
            else
            {
                currentStatusCode = -1;
                await _publisher.PublishDeviceStatusAsync(_device.DeviceId, $"连接失败: {connectResult.Message}", -1);
                lastStatusPublishTime = DateTime.Now;

                consecutiveFailures++;
            }

            // 进入死循环轮询
            while (!token.IsCancellationRequested)
            {
                // 🟢 1. 熔断判定：如果连续失败超过 3 次，进入隔离休眠期！
                if (consecutiveFailures >= 3)
                {
                    _logger.LogWarning("⚠️ 设备 [{Device}] 连续失败3次，触发熔断隔离！强制休眠 30 秒...", _device.DeviceName);

                    await _publisher.PublishDeviceStatusAsync(_device.DeviceId, "设备失联(熔断隔离中)", -1);
                    try { await Task.Delay(30000, token); } catch { break; }

                    // 睡醒后，给一次重新证明自己的机会（半开状态）
                    consecutiveFailures = 2;

                    // 尝试重新建立底层 TCP 连接
                    _plcClient.ConnectClose();
                    await _plcClient.ConnectServerAsync();
                }

                // 🟢 2. 新兵免检期判定
                bool isStartupBypass = false;
                if (startupBypassCount > 0)
                {
                    isStartupBypass = true;
                    startupBypassCount--;
                }

                bool allPointsFailed = true; // 悲观假设：默认全军覆没
                string lastError = "";
                bool isAnyPointProcessedThisCycle = false; // 记录这一轮“心跳”中，到底有没有真去读数据

                // 排队获取全局网络锁
                await _concurrencyLock.WaitAsync(token);

                try
                {
                    // ==========================================
                    // 🟢 通道 A：极速批量读取通道 (内存切片魔法)
                    // ==========================================
                    foreach (var group in _batchGroups)
                    {
                        if (token.IsCancellationRequested) break;

                        OperateResult<byte[]> readBlockResult = _plcClient.Read(group.StartAddress, group.TotalLength);

                        if (!readBlockResult.IsSuccess)
                        {
                            lastError = readBlockResult.Message;
                            continue;
                        }

                        allPointsFailed = false;
                        byte[] buffer = readBlockResult.Content;

                        foreach (var meta in group.Points)
                        {
                            var point = meta.Point;
                            if ((DateTime.Now - point.LastScanTime).TotalMilliseconds < point.ScanIntervalMs) continue;

                            isAnyPointProcessedThisCycle = true;
                            point.LastScanTime = DateTime.Now;

                            object? parsedValue = null;
                            bool sliceSuccess = true;
                            string sliceError = "";

                            try
                            {
                                if (meta.ByteOffset + meta.ByteLength <= buffer.Length)
                                {
                                    switch (point.DataType)
                                    {
                                        case DataTypeEnum.Short: parsedValue = _plcClient.ByteTransform.TransInt16(buffer, meta.ByteOffset); break;
                                        case DataTypeEnum.Int: parsedValue = _plcClient.ByteTransform.TransInt32(buffer, meta.ByteOffset); break;
                                        case DataTypeEnum.Float: parsedValue = _plcClient.ByteTransform.TransSingle(buffer, meta.ByteOffset); break;
                                 
                                        case DataTypeEnum.String:
                                            parsedValue = _plcClient.ByteTransform.TransString(buffer, meta.ByteOffset, point.Length, Encoding.UTF8)?.TrimEnd('\0');
                                            break;
                                    }
                                }
                                else { sliceSuccess = false; sliceError = "内存切片越界"; }
                            }
                            catch (Exception ex) { sliceSuccess = false; sliceError = $"解析异常: {ex.Message}"; }

                            OperateResult<object> rawResult = sliceSuccess && parsedValue != null
                                ? OperateResult.CreateSuccessResult(parsedValue)
                                : new OperateResult<object>(sliceError);

                            StandardPointData processedData = _processor.Process(_device, point, rawResult);
                            if (processedData.HasChanged || !processedData.IsSuccess || isStartupBypass)
                            {
                                await _publisher.PublishPointDataAsync(processedData);
                            }
                        }
                    }

                    // ==========================================
                    // 🟢 通道 B：单点补漏通道 (伺候 Bool, String 和非 Modbus 设备)
                    // ==========================================
                    // 🚨 移除了 else！紧接着通道 A 之后执行，绝不漏掉一个！
                    foreach (var point in _unbatchedPoints)
                    {
                        if (token.IsCancellationRequested) break;

                        if ((DateTime.Now - point.LastScanTime).TotalMilliseconds < point.ScanIntervalMs)
                            continue;

                        isAnyPointProcessedThisCycle = true;
                        OperateResult<object> readResult = ReadPointValue(point);
                        point.LastScanTime = DateTime.Now;

                        if (readResult.IsSuccess) allPointsFailed = false;
                        else lastError = readResult.Message;

                        StandardPointData processedData = _processor.Process(_device, point, readResult);

                        if (processedData.HasChanged || !processedData.IsSuccess || isStartupBypass)
                        {
                            await _publisher.PublishPointDataAsync(processedData);
                        }
                    }
                }
                finally
                {
                    _concurrencyLock.Release();
                }

                // 🟢 3. 核心诊断：这一轮扫完后，评估设备的宏观健康状态
                if (isAnyPointProcessedThisCycle && _device.Points.Count > 0 && !token.IsCancellationRequested)
                {
                    if (allPointsFailed)
                    {
                        consecutiveFailures++; // 失败累加
                    }
                    else
                    {
                        consecutiveFailures = 0; // 只要能读通哪怕一个点，立刻清零恢复健康！
                    }

                    int newStatusCode = allPointsFailed ? -1 : 1;

                    // 保活心跳机制：状态发生跳变，或者距离上次发送超过了 30 秒
                    bool isStatusChanged = newStatusCode != currentStatusCode;
                    bool isHeartbeatTime = (DateTime.Now - lastStatusPublishTime).TotalSeconds >= 30;

                    if (isStatusChanged || isHeartbeatTime)
                    {
                        currentStatusCode = newStatusCode;
                        string statusText = currentStatusCode == 1 ? "在线/采集中" : $"连接异常/断线: {lastError}";

                        await _publisher.PublishDeviceStatusAsync(_device.DeviceId, statusText, currentStatusCode);
                        lastStatusPublishTime = DateTime.Now;

                        if (isStatusChanged)
                        {
                            _logger.LogWarning("设备 [{Device}] 状态发生跳变 -> {Status}", _device.DeviceName, statusText);
                        }
                    }
                }

                // 严格遵守基础扫描周期 (怠速)，并捕获取消异常
                try
                {
                    await Task.Delay(_device.ScanIntervalMs, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            // 循环被取消（如重新下发配置或程序退出时），通知 UI 变灰
            await _publisher.PublishDeviceStatusAsync(_device.DeviceId, "已停止", 0);
        }

        /// <summary>
        /// 核心读取：严格基于 HSL 的知识库规范进行泛型结果转换 (单点降级通道专用)
        /// </summary>
        private OperateResult<object> ReadPointValue(PointConfig point)
        {
            switch (point.DataType)
            {
                case DataTypeEnum.Int:
                    OperateResult<int> resInt32 = _plcClient.ReadInt32(point.Address);
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

                case DataTypeEnum.String:
                    OperateResult<string> resString = _plcClient.ReadString(point.Address, point.Length, Encoding.UTF8);
                    if (!resString.IsSuccess) return OperateResult.CreateFailedResult<object>(resString);
                    string cleanString = resString.Content?.TrimEnd('\0');
                    return OperateResult.CreateSuccessResult<object>(cleanString);

                default:
                    return new OperateResult<object>($"Worker 不支持的点位数据类型: {point.DataType}");
            }
        }

        /// <summary>
        /// 实例化真实存在的协议客户端
        /// </summary>
        private DeviceTcpNet CreatePlcClient(DeviceConfig device)
        {
            switch (device.ProtocolType)
            {
                case ProtocolTypeEnum.ModbusTCP:
                    var modbus = new ModbusTcpNet(device.IpAddress, device.Port);

                    // 🟢 1. 解决 0和1 的偏移大坑
                    modbus.AddressStartWithZero = device.IsAddressStartWithZero;

                    // 🟢 2. 解决 ABCD 字节序乱码大坑！
                    // HSL 的 ByteTransform 极其强大，只要在这里配好，
                    // 无论是单点 ReadFloat，还是前面写的批量内存切片 TransSingle，都会自动用这个字节序去切！
                    modbus.DataFormat = device.DataFormat switch
                    {
                        DataFormatEnum.ABCD => HslCommunication.Core.DataFormat.ABCD,
                        DataFormatEnum.BADC => HslCommunication.Core.DataFormat.BADC,
                        DataFormatEnum.CDAB => HslCommunication.Core.DataFormat.CDAB,
                        DataFormatEnum.DCBA => HslCommunication.Core.DataFormat.DCBA,
                        _ => HslCommunication.Core.DataFormat.CDAB
                    };
                    return modbus;

                case ProtocolTypeEnum.S71200:
                    // 西门子的字节序是极其严格和标准的，不需要像 Modbus 那么乱配
                    return new SiemensS7Net(SiemensPLCS.S1200, device.IpAddress) { Port = device.Port };

                default:
                    throw new NotSupportedException($"不支持的协议类型: {device.ProtocolType}");
            }
        }
    }
}