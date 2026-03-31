

    using global::Collector.Edge.Configuration;
    using global::Collector.Edge.Processing;
    using global::Collector.Edge.Publishing;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Concurrent;
    using System.Threading.Tasks;

    namespace Collector.Edge.Engine
    {
        public class CollectionEngine : ICollectionEngine
        {
            private readonly IConfigManager _configManager;
            private readonly IDataProcessor _processor;
            private readonly IMqttPublisher _publisher;
            private readonly ILoggerFactory _loggerFactory; // 注意：这里注入工厂， 为了给每个 Worker 单独发一个日志记录器
            private readonly ILogger<CollectionEngine> _logger;

        // 🟢 全局并发控制器：全厂最多允许 50 个并发网络 IO！(具体数值可配)
        private readonly SemaphoreSlim _globalConcurrencyLock = new SemaphoreSlim(20, 20);

        // 核心花名册：记录当前正在干活的所有工人 (Key: DeviceId, Value: Worker实例)
        private readonly ConcurrentDictionary<string, DeviceCollectWorker> _workers = new();

            public CollectionEngine(
                IConfigManager configManager,
                IDataProcessor processor,
                IMqttPublisher publisher,
                ILoggerFactory loggerFactory,
                ILogger<CollectionEngine> logger)
            {
                _configManager = configManager;
                _processor = processor;
                _publisher = publisher;
                _loggerFactory = loggerFactory;
                _logger = logger;
            }

            public Task StartAsync()
            {
                _logger.LogInformation("🚀 引擎总控收到 [启动] 指令，准备安排开工...");
                return ReloadAsync(); // 启动的本质其实就是按最新配置拉起一波
            }

            public async Task StopAsync()
            {
                _logger.LogInformation("🛑 引擎总控收到 [停止] 指令，正在遣散所有 Worker...");

                foreach (var kvp in _workers)
                {
                    try
                    {
                     await   kvp.Value.StopAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "停止设备 [{DeviceId}] 的 Worker 时发生异常！", kvp.Key);
                    }
                }

                _workers.Clear();
                _logger.LogInformation("✅ 所有采集 Worker 已安全停止。");

              
            }

            public async Task ReloadAsync()
            {
                _logger.LogInformation("🔄 引擎总控收到 [热重载] 指令...");

                // 1. 先安全停止并清空当前所有的旧工人
              await StopAllAsync();

                // 2. 从防弹档案柜 (ConfigManager) 里拿出最新的一批图纸
                var configs = _configManager.GetCurrentConfigs();
                if (configs == null || configs.Count == 0)
                {
                    _logger.LogWarning("当前没有任何设备配置，引擎进入待机状态。");
                   
                }

                // 3. 按图纸逐个雇佣新工人，并分配给他们专属的日志记录器
                foreach (var deviceConfig in configs)
                {
                    var workerLogger = _loggerFactory.CreateLogger<DeviceCollectWorker>();
                // 🟢 把全局锁发给每一个工人
                var worker = new DeviceCollectWorker(deviceConfig, _processor, _publisher, workerLogger, _globalConcurrencyLock);

                if (_workers.TryAdd(deviceConfig.DeviceId, worker))
                    {
                       
                        worker.Start();
                    }
                    else
                    {
                        _logger.LogWarning("设备 [{DeviceId}] 存在重复配置，已忽略！", deviceConfig.DeviceId);
                    }
                }

                _logger.LogInformation("🎉 引擎热重载完成！当前正在运行的 Worker 数量: {Count}", _workers.Count);

               
            }


        // 🟢 补全：真正的 StopAll 实现
        public async Task StopAllAsync()
        {
            if (_workers.IsEmpty)
            {
                _logger.LogInformation("当前没有运行中的采集任务，无需停止。");
                return;
            }

            _logger.LogWarning("🚨 收到紧急停止指令，车间主任开始强制遣散所有工人...");

            // 收集所有工人的退出任务
            var stopTasks = _workers.Values.Select(w => w.StopAsync());

            // 🟢 核心：并行等待所有工人彻底死透！
            await Task.WhenAll(stopTasks);

            // 2. 彻底清空花名册
            _workers.Clear();

            _logger.LogInformation("🛑 所有车间采集流水线已全部清空并停止运转。");
        }
    }
    }

