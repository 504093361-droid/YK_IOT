using Collector.Contracts;
using Collector.Contracts.Enums;
using Collector.Contracts.Model;
using Collector.Contracts.Topics;
using Collector.Edge.Processing;
using Collector.Edge.Publishing;
using Contracts.Interface;
using HslCommunication;
using HslCommunication.Core.Device;
using HslCommunication.Core.Net;
using HslCommunication.ModBus;
using HslCommunication.Profinet.Siemens;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Collector.Edge.Engine
{
    /// <summary>
    /// 🚀 工业级设备采集核心引擎 (Worker)
    /// 【架构设计】：每个独立设备对应一个独立实例。内置双通道采集机制、断线熔断、点位级故障隔离、差异化防抖发布。
    /// </summary>
    public class PollingCollectWorker: ICollectWorker
    {
        // 基础服务注入
        private readonly DeviceConfig _device;
        private readonly IDataProcessor _processor;
        private readonly IMqttPublisher _publisher;
        private readonly ILogger _logger;

        // 【绝对严谨】：严格使用 HslCommunication 的真实基类 DeviceTcpNet，完美兼容所有基于 TCP 的 PLC 协议
        private DeviceTcpNet _plcClient;

        // 线程控制与取消令牌
        private CancellationTokenSource _cts;
        private Task _pollingTask;

        // 🟢 全局并发锁：防止上百个 Worker 同时向局域网发起 TCP 请求，引发交换机或网关的物理级网络风暴 (DDOS)
        private readonly SemaphoreSlim _concurrencyLock;

        // =========================================
        // 🚀 双通道架构缓存区
        // =========================================
        // 🟢 通道 A 缓存：合包策略缓存 (专门存那些被打包成连续大块的数值型点位，享受内存极速切片)
        private List<BatchReadGroup> _batchGroups = new();

        // 🟢 通道 B 缓存：单点补漏名单 (专门存 Bool、String、跨度极大的离散点，或者完全不支持合包的协议如西门子)
        private List<PointConfig> _unbatchedPoints = new();

        // =========================================
        // ⚙️ 统一配置中心 (无缝热重载机制)
        // =========================================
        // 🟢 告别魔法数字：通过 IOptionsMonitor 实时监听 appsettings.json，改动无需重启即可生效
        private readonly IOptionsMonitor<SystemOptions> _sysOptions;

        // =========================================
        // 🛡️ 异常点位降级与隔离防线 (冷宫机制)
        // =========================================
        // 🟢 点位健康档案：记录某个点位连续读失败的次数 (Key: PointId, Value: 连续失败次数)
        private readonly Dictionary<string, int> _pointFailureCounts = new();

        // 🟢 禁闭解除时间表：记录坏点位何时能被释放出来再次尝试读取 (Key: PointId, Value: 解禁时间)
        private readonly Dictionary<string, DateTime> _pointRetryNextTime = new();

        // 🟢 防报错风暴缓存：记录上次失败的原因。只要原因不变，就只报一次，死死掐断无意义的 MQTT 垃圾流量
        private readonly Dictionary<string, string> _lastErrorCache = new();


        public PollingCollectWorker(
            DeviceConfig device,
            IDataProcessor processor,
            IMqttPublisher publisher,
            ILogger logger,
            SemaphoreSlim concurrencyLock,
            IOptionsMonitor<SystemOptions> sysOptions)
        {
            _device = device;
            _processor = processor;
            _publisher = publisher;
            _logger = logger;
            _concurrencyLock = concurrencyLock;
            _sysOptions = sysOptions;
        }

        public async Task StartAsync()
        {
            _logger.LogInformation("准备启动设备 [{DeviceName}] 的采集 Worker...", _device.DeviceName);

            // 工厂模式：根据设备协议创建真实的底层通讯客户端 (ModbusTcpNet / SiemensS7Net)
            _plcClient = CreatePlcClient(_device);
            _cts = new CancellationTokenSource();

            // 🚀 核心架构分流：合包优化引擎启动
            if (_device.ProtocolType == ProtocolTypeEnum.ModbusTCP)
            {
                // 1. 将杂乱的点位交给优化器，算出最优的内存连读块 (例如读 40001~40100)
                _batchGroups = ModbusBatchOptimizer.Optimize(_device.Points);

                // 2. 核心修复：把那些被合包算法踢出来的“特殊点位”（比如Bool/String），挑出来放进单点通道
                var batchedPointIds = new HashSet<string>();
                foreach (var g in _batchGroups)
                    foreach (var p in g.Points)
                        batchedPointIds.Add(p.Point.PointId);

                foreach (var p in _device.Points)
                {
                    // 如果这个点位没有被合包优化器选中，就把它丢进单点补漏名单
                    if (!batchedPointIds.Contains(p.PointId))
                        _unbatchedPoints.Add(p);
                }

                _logger.LogInformation("🚀 极速引擎挂载：[{Device}] 共 {pCount} 个点位，{gCount} 组进入批量通道(A)，{uCount} 个进入单点通道(B)！",
                    _device.DeviceName, _device.Points.Count, _batchGroups.Count, _unbatchedPoints.Count);
            }
            else
            {
                // ⚠️ 向下兼容：如果是西门子等目前还没写合包算法的设备，所有点位全部退化进入单点通道(通道B)
                _unbatchedPoints.AddRange(_device.Points);
            }

            // 启动后台死循环轮询任务
            _pollingTask = Task.Run(() => PollingLoopAsync(_cts.Token), _cts.Token);
        }

        public async Task StopAsync()
        {
            _logger.LogInformation("正在停止设备 [{DeviceName}] 的采集 Worker...", _device.DeviceName);

            // 发送取消信号，掐断底层的 while 死循环
            _cts?.Cancel();

            // 掐断底层的物理 TCP Socket 连接
            _plcClient?.ConnectClose();

            // 🟢 优雅停机核心：必须等待 _pollingTask 跑完最后一遍（比如把它的离线遗言发完），再真正销毁实例
            if (_pollingTask != null)
            {
                try { await _pollingTask; } catch { /* 忽略由于 Cancel() 引发的任务取消异常 */ }
            }
        }

        private async Task PollingLoopAsync(CancellationToken token)
        {
            // --- 状态追踪器 ---
            int currentStatusCode = 0;  // 当前设备状态码 (-1:断线, 0:停止, 1:正常)
            DateTime lastStatusPublishTime = DateTime.MinValue;  // 上次向云端汇报心跳的时间

            // --- 容错与重试机制 ---
            // 新兵免检期：系统刚启动时，前几次采集无视死区，强制全量推送到云端，防止云端 UI 出现空白
            int startupBypassCount = _sysOptions.CurrentValue.StartupBypassCount;

            // 设备级连续失败计数器：用于触发熔断隔离，防止死磕掉线的设备拖垮整个线程池
            int consecutiveFailures = 0;

            // ====================================
            // 开场白：首次主动尝试连接设备
            // =====================================
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

            // =====================================
            // ♾️ 永不休止的轮询死循环 (核心战场)
            // =====================================
            while (!token.IsCancellationRequested)
            {
                // 🟢 1. 设备级熔断判定：如果连续失败超过配置阈值，进入深度隔离休眠！
                if (consecutiveFailures >= _sysOptions.CurrentValue.MaxRetryCount)
                {
                    _logger.LogWarning("⚠️ 设备 [{Device}] 连续失败{Count}次，触发熔断隔离！强制休眠 {Ms} 毫秒...",
                        _device.DeviceName, _sysOptions.CurrentValue.MaxRetryCount, _sysOptions.CurrentValue.CircuitBreakerDelayMs);

                    await _publisher.PublishDeviceStatusAsync(_device.DeviceId, "设备失联(熔断隔离中)", -1);

                    // 睡死过去，不再发任何无意义的 TCP 请求占用带宽
                    try { await Task.Delay(_sysOptions.CurrentValue.CircuitBreakerDelayMs, token); } catch { break; }

                    // 醒来后进入“半开(Half-Open)”状态：再给你一次机会去连，连不上立刻继续睡
                    consecutiveFailures = _sysOptions.CurrentValue.MaxRetryCount - 1;

                    // 暴力清理旧的 TCP 句柄，重新握手
                    _plcClient.ConnectClose();
                    await _plcClient.ConnectServerAsync();
                }

                // 🟢 2. 新兵免检期倒计时判定
                bool isStartupBypass = false;
                if (startupBypassCount > 0)
                {
                    isStartupBypass = true;
                    startupBypassCount--;
                }

                // 宏观战报追踪：悲观假设本轮全部失败，只要有一个点读通了，这台设备就是活着的
                bool allPointsFailed = true;
                string lastError = "";
                bool isAnyPointProcessedThisCycle = false;

                // 🚥 排队获取全局网络锁 (防止上百个 Worker 把以太网卡打穿)
                await _concurrencyLock.WaitAsync(token);

                try
                {
                    // ==========================================
                    // 🟢 通道 A：极速批量读取通道 (内存切片魔法)
                    // ==========================================
                    foreach (var group in _batchGroups)
                    {
                        if (token.IsCancellationRequested) break;

                        // 发起一击必杀的块读取 (比如一次读 100 个寄存器)
                        OperateResult<byte[]> readBlockResult = _plcClient.Read(group.StartAddress, group.TotalLength);

                        if (!readBlockResult.IsSuccess)
                        {
                            lastError = readBlockResult.Message;
                            continue; // 块读取失败，直接放弃这几十个点，极其节省性能
                        }

                        // 只要块读回来了，证明网络是通的
                        allPointsFailed = false;
                        byte[] buffer = readBlockResult.Content;

                        // 开始在内存里“切蛋糕”，把一大块 byte[] 切成一个个具体的数值
                        foreach (var meta in group.Points)
                        {
                            var point = meta.Point;

                            // 尊重每个点位独立配置的快慢周期
                            if ((DateTime.Now - point.LastScanTime).TotalMilliseconds < point.ScanIntervalMs) continue;

                            // 🟢 [极其重要的新防线]：点位级故障隔离
                            // 如果这个点位处于冷宫禁闭期，直接跳过内存解析，防止有毒数据扰乱后续逻辑
                            if (_pointRetryNextTime.TryGetValue(point.PointId, out var nextTime) && DateTime.Now < nextTime) continue;

                            isAnyPointProcessedThisCycle = true;
                            point.LastScanTime = DateTime.Now;

                            object? parsedValue = null;
                            bool sliceSuccess = true;
                            string sliceError = "";

                            try
                            {
                                // 防止偏移量写错导致数组越界崩溃
                                if (meta.ByteOffset + meta.ByteLength <= buffer.Length)
                                {
                                    // 🔪 纯内存级切割，快到 CPU 冒烟 (0.00x毫秒级)
                                    switch (point.DataType)
                                    {
                                        case DataTypeEnum.Short: parsedValue = _plcClient.ByteTransform.TransInt16(buffer, meta.ByteOffset); break;
                                        case DataTypeEnum.Int: parsedValue = _plcClient.ByteTransform.TransInt32(buffer, meta.ByteOffset); break;
                                        case DataTypeEnum.Float: parsedValue = _plcClient.ByteTransform.TransSingle(buffer, meta.ByteOffset); break;

                                        // 完美切割中文和英文字符串，并在尾部截断 PLC 为了凑寄存器而补的空白符 \0
                                        case DataTypeEnum.String:
                                            parsedValue = _plcClient.ByteTransform.TransString(buffer, meta.ByteOffset, point.Length, Encoding.UTF8)?.TrimEnd('\0');
                                            break;
                                    }
                                }
                                else { sliceSuccess = false; sliceError = "内存切片越界，可能配置的长度超出了读取范围"; }
                            }
                            catch (Exception ex) { sliceSuccess = false; sliceError = $"解析异常: {ex.Message}"; }

                            // 组装最终战报
                            OperateResult<object> rawResult = sliceSuccess && parsedValue != null
                                ? OperateResult.CreateSuccessResult(parsedValue)
                                : new OperateResult<object>(sliceError);

                            // 交给中央处理器 (死区过滤、公式计算、比例偏移)
                            StandardPointData processedData = _processor.Process(_device, point, rawResult);

                            // 🟢 核心委托：调用防风暴智能发布
                            await HandlePointPublishingAsync(point, processedData, isStartupBypass);
                        }
                    }

                    // ==========================================
                    // 🟢 通道 B：单点补漏通道 (伺候 Bool, String 和没有优化器的设备)
                    // ==========================================
                    foreach (var point in _unbatchedPoints)
                    {
                        if (token.IsCancellationRequested) break;

                        if ((DateTime.Now - point.LastScanTime).TotalMilliseconds < point.ScanIntervalMs)
                            continue;

                        // 🟢 [极其重要的新防线]：点位隔离冷宫判定
                        // 这是专门针对比如错配的 90 个一直超时的 Bool 量设计的。
                        // 如果不跳过，这 90 个点会在这里死等超时，导致一次循环卡死 90 秒！现在有了它，瞬间越过坏点！
                        if (_pointRetryNextTime.TryGetValue(point.PointId, out var nextTime) && DateTime.Now < nextTime)
                            continue;

                        isAnyPointProcessedThisCycle = true;

                        // 发起极其昂贵的单点 TCP 请求
                        OperateResult<object> readResult = ReadPointValue(point);
                        point.LastScanTime = DateTime.Now;

                        if (readResult.IsSuccess) allPointsFailed = false;
                        else lastError = readResult.Message;

                        StandardPointData processedData = _processor.Process(_device, point, readResult);

                        // 🟢 核心委托：调用防风暴智能发布
                        await HandlePointPublishingAsync(point, processedData, isStartupBypass);
                    }
                }
                finally
                {
                    // 无论发生什么，绝对不能死锁，必须释放全局网络锁
                    _concurrencyLock.Release();
                }

                // ==========================================
                // 🟢 3. 核心诊断：这一轮扫完后，评估设备的宏观健康状态
                // ==========================================
                if (isAnyPointProcessedThisCycle && _device.Points.Count > 0 && !token.IsCancellationRequested)
                {
                    if (allPointsFailed)
                    {
                        consecutiveFailures++; // 全部覆没，设备失败次数累加
                    }
                    else
                    {
                        consecutiveFailures = 0; // 只要能读通哪怕一个点，立刻证明通讯底座是健康的，清零！
                    }

                    int newStatusCode = allPointsFailed ? -1 : 1;

                    // 状态跳变检测
                    bool isStatusChanged = newStatusCode != currentStatusCode;
                    // 保活心跳检测：避免云端觉得这台设备“死”了
                    bool isHeartbeatTime = (DateTime.Now - lastStatusPublishTime).TotalSeconds >= _sysOptions.CurrentValue.HeartbeatIntervalSeconds;

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

                // 严格遵守基础扫描周期 (设备的怠速节拍)，防止 CPU 100% 空转
                try
                {
                    await Task.Delay(_device.ScanIntervalMs, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            // 循环被外界打断（如修改配置引发的热重启，或程序退出），通知 UI 此设备变灰
            await _publisher.PublishDeviceStatusAsync(_device.DeviceId, "已停止", 0);
        }

        /// <summary>
        /// 🟢 核心重构：防报错风暴智能发布 与 点位自动降级(冷宫)管理器
        /// </summary>
        private async Task HandlePointPublishingAsync(PointConfig point, StandardPointData processedData, bool isStartupBypass)
        {
            bool shouldPublish = false;

            if (processedData.IsSuccess)
            {
                // ✅ 采集成功：证明它是个好点。恢复信用，清理一切犯罪记录
                _pointFailureCounts[point.PointId] = 0;
                _pointRetryNextTime.Remove(point.PointId);
                _lastErrorCache.Remove(point.PointId);

                // 只有数据真变了才发（或者刚开机的免检期）—— 【极其恐怖的云端省流大法】
                if (processedData.HasChanged || isStartupBypass)
                    shouldPublish = true;
            }
            else
            {
                // ❌ 采集失败：开始记小本本
                _pointFailureCounts.TryGetValue(point.PointId, out int fails);
                fails++;
                _pointFailureCounts[point.PointId] = fails;

                // 从配置中提取冷宫判定的参数 (保证没有魔法数字)
                int maxRetry = _sysOptions.CurrentValue.PointMaxRetryBeforeQuarantine > 0 ? _sysOptions.CurrentValue.PointMaxRetryBeforeQuarantine : 3;
                int quarantineSec = _sysOptions.CurrentValue.PointQuarantineDurationSeconds > 0 ? _sysOptions.CurrentValue.PointQuarantineDurationSeconds : 60;

                // 如果连续错够了次数，直接送入冷宫
                if (fails >= maxRetry)
                {
                    _pointRetryNextTime[point.PointId] = DateTime.Now.AddSeconds(quarantineSec);
                    _logger.LogTrace("点位 [{Point}] 连续失败达阈值，进入 {Sec} 秒冷宫隔离！", point.PointName, quarantineSec);
                }

                // 🔴 防报错风暴的核心：如果这个点位每次循环都报一模一样的错，坚决不上报 MQTT！
                // 只有错误消息内容发生变化（比如从"超时"变成了"无访问权限"），才允许上报一次！
                _lastErrorCache.TryGetValue(point.PointId, out string lastErr);
                if (lastErr != processedData.ErrorMessage)
                {
                    shouldPublish = true;
                    _lastErrorCache[point.PointId] = processedData.ErrorMessage;
                }
            }

            // 【后续伏笔】：引入 Channels 之后，下面这句话会换成写入通道，彻底消灭并发网络风暴！
            if (shouldPublish)
            {
                await _publisher.PublishPointDataAsync(processedData);
            }
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

                    // 西门子或 Modbus 里的字符串，经常会在有效数据后面补 \0 占位符，必须剔除
                    string cleanString = resString.Content?.TrimEnd('\0');
                    return OperateResult.CreateSuccessResult<object>(cleanString);

                default:
                    return new OperateResult<object>($"Worker 不支持的点位数据类型: {point.DataType}");
            }
        }

        /// <summary>
        /// 实例化真实存在的协议客户端 (底层基石)
        /// </summary>
        private DeviceTcpNet CreatePlcClient(DeviceConfig device)
        {
            switch (device.ProtocolType)
            {
                case ProtocolTypeEnum.ModbusTCP:
                    var modbus = new ModbusTcpNet(device.IpAddress, device.Port);


                    // 🟢 极其关键：打通物理路由的最后一公里 —— 站号 (Unit Identifier)
                    // HSL 的基类支持直接给 TCP 模式赋予 Station，这会写进 MBAP 报文头的第 6 个字节
                    modbus.Station = device.Station;

                    // 🟢 解决 0 和 1 的偏移大坑
                    modbus.AddressStartWithZero = device.IsAddressStartWithZero;

                    // 🟢 解决 ABCD 字节序乱码大坑！
                    // HSL 的 ByteTransform 极其强大，只要在这里配好，
                    // 无论是单点 ReadFloat，还是前面批量极速内存切片时的 TransSingle，都会自动遵守这个字节序进行翻译！
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
                    // 西门子的字节序是业界最统一的(大端序)，不需要像 Modbus 那样疯狂配字节序
                    return new SiemensS7Net(SiemensPLCS.S1200, device.IpAddress) { Port = device.Port };

                default:
                    throw new NotSupportedException($"不支持的协议类型: {device.ProtocolType}");
            }
        }
    }
}