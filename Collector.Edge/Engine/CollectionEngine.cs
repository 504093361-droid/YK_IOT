using Collector.Contracts.Model;
using Collector.Contracts.Enums;
using Contracts.Interface; // 🟢 引入 ICollectWorker 接口
using global::Collector.Edge.Configuration;
using global::Collector.Edge.Processing;
using global::Collector.Edge.Publishing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Linq; // 🟢 必须引入，为了支持 StopAllAsync 中的 Select 语法
using System.Threading.Tasks;

namespace Collector.Edge.Engine
{
    /// <summary>
    /// 🏭 采集引擎大管家 (车间主任)
    /// 负责解析配置图纸，动态招募、启停不同兵种的采集 Worker，并控制全厂并发量。
    /// </summary>
    public class CollectionEngine : ICollectionEngine
    {
        private readonly IConfigManager _configManager;
        private readonly IDataProcessor _processor;
        private readonly IMqttPublisher _publisher;
        private readonly ILoggerFactory _loggerFactory; // 为了给每个 Worker 单独发一个日志记录器
        private readonly ILogger<CollectionEngine> _logger;
        private readonly IOptionsMonitor<SystemOptions> _sysOptions;

        // 🟢 全局并发控制器：全厂最多允许 20 个并发网络 IO！(具体数值可配)
        // 专门用来勒住那些疯狂死循环轮询的传统协议，防止把厂区交换机打穿。
        private readonly SemaphoreSlim _globalConcurrencyLock = new SemaphoreSlim(20, 20);

        // 🟢 核心花名册 (极其关键的重构)：
        // Value 从具体的 PollingCollectWorker 提升为抽象的 ICollectWorker！
        // 这样这个字典就能同时装下 "死循环轮询型工人" 和 "事件驱动型工人" 了。
        private readonly ConcurrentDictionary<string, ICollectWorker> _workers = new();

        public CollectionEngine(
            IConfigManager configManager,
            IDataProcessor processor,
            IMqttPublisher publisher,
            ILoggerFactory loggerFactory,
            ILogger<CollectionEngine> logger,
            IOptionsMonitor<SystemOptions> sysOptions)
        {
            _configManager = configManager;
            _processor = processor;
            _publisher = publisher;
            _loggerFactory = loggerFactory;
            _logger = logger;
            _sysOptions = sysOptions;
        }

        public Task StartAsync()
        {
            _logger.LogInformation("🚀 引擎总控收到 [启动] 指令，准备安排开工...");
            return ReloadAsync(); // 启动的本质其实就是按最新配置拉起一波
        }

        public async Task StopAsync()
        {
            _logger.LogInformation("🛑 引擎总控收到 [停止] 指令，正在遣散所有 Worker...");
            await StopAllAsync();
        }

        public async Task ReloadAsync()
        {
            _logger.LogInformation("🔄 引擎总控收到 [热重载] 指令...");

            // 1. 先安全停止并清空当前所有的旧工人 (确保没有任何残留的 TCP 句柄或 OPC 订阅)
            await StopAllAsync();

            // 2. 从防弹档案柜 (ConfigManager) 里拿出最新的一批图纸
            var configs = _configManager.GetCurrentConfigs();
            if (configs == null || configs.Count == 0)
            {
                _logger.LogWarning("当前没有任何设备配置，引擎进入待机状态。");
                return; // 保护逻辑：没图纸直接返回
            }

            // 3. 🟢 智能派单中心：按图纸的协议类型，逐个雇佣【不同兵种】的新工人
            foreach (var deviceConfig in configs)
            {
                ICollectWorker worker = null;

                try
                {
                    // 策略模式 (Strategy)：根据协议分发不同的引擎
                    if (deviceConfig.ProtocolType == ProtocolTypeEnum.OPC_UA)
                    {
                        // 招募 OPC UA 特种兵 (无需发并发锁，因为它是优雅的订阅制)
                        var opcLogger = _loggerFactory.CreateLogger<OpcUaCollectWorker>();
                        worker = new OpcUaCollectWorker(deviceConfig, _publisher, opcLogger,_processor, _sysOptions);
                    }
                    else
                    {
                        // 招募传统死循环老兵 (必须发 _globalConcurrencyLock 全局锁限制并发)
                        var pollingLogger = _loggerFactory.CreateLogger<PollingCollectWorker>();
                        worker = new PollingCollectWorker(deviceConfig, _processor, _publisher, pollingLogger, _globalConcurrencyLock, _sysOptions);
                    }

                    // 把新工人登记入册
                    if (_workers.TryAdd(deviceConfig.DeviceId, worker))
                    {
                        // 🟢 使用全新的全异步 StartAsync
                        await worker.StartAsync();
                    }
                    else
                    {
                        _logger.LogWarning("设备 [{DeviceId}] 存在重复配置，已忽略！", deviceConfig.DeviceId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "为设备 [{DeviceId}] 招募/启动 Worker 时发生致命异常！", deviceConfig.DeviceId);
                }
            }

            _logger.LogInformation("🎉 引擎热重载完成！当前正在运行的 Worker 数量: {Count}", _workers.Count);
        }

        // 🟢 真正的 StopAll 实现 (并行优雅停机)
        public async Task StopAllAsync()
        {
            if (_workers.IsEmpty)
            {
                _logger.LogInformation("当前没有运行中的采集任务，无需停止。");
                return;
            }

            _logger.LogWarning("🚨 收到清理指令，车间主任开始强制遣散所有工人...");

            // 收集所有工人的退出任务 (无论是 OPC UA 释放 Session，还是 Modbus 断开 TCP)
            var stopTasks = _workers.Values.Select(w => w.StopAsync());

            // 🟢 核心：并行等待所有工人彻底死透，绝不让任何一个线程变成无主孤魂！
            await Task.WhenAll(stopTasks);

            // 彻底清空花名册
            _workers.Clear();

            _logger.LogInformation("🛑 所有车间采集流水线已全部清空并停止运转。");
        }
    }
}