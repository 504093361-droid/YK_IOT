using Collector.Contracts.Topics;
using Collector.Edge.Control;
using Contracts.Interface; // 引入 IMqttService
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Collector.Edge.Messaging
{
    public class MqttCommandReceiver : BackgroundService
    {
        private readonly IEdgeController _controller;
        private readonly IMqttService _mqttService; // 注入大管家
        private readonly ILogger<MqttCommandReceiver> _logger;

        public MqttCommandReceiver(IEdgeController controller, IMqttService mqttService, ILogger<MqttCommandReceiver> logger)
        {
            _controller = controller;
            _mqttService = mqttService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
           
            // 所以我们把它强制转换为它真正的实体 EdgeMqttService
          
                // 1. 挂载接收事件
                _mqttService.OnMessageReceived += async (topic, payload) =>
                {
                    Console.WriteLine($"\n[超级调试] 听到消息 -> 主题: {topic}");

                    if (topic == CollectorTopics.ConfigUpdate)
                    {
                        _logger.LogInformation("耳朵 (Messaging) 听到配置更新主题消息，甩给大脑...");
                        await _controller.HandleConfigUpdatedAsync(payload);
                    }

                    // 🟢 场景 2：收到启停控制指令
                    else if (topic == CollectorTopics.EngineControl)
                    {
                        if (payload.ToLower() == "stop")
                        {
                            _logger.LogWarning("🚨 接收到紧急停止指令！即将停止所有采集任务...");
                            await _controller.StopEngineAsync(); // 呼叫大脑停止
                        }
                    }

                };

                // 2. 主动发起连接并订阅配置主题
                await _mqttService.ConnectAsync();
                await _mqttService.SubscribeAsync(CollectorTopics.ConfigUpdate);
                await _mqttService.SubscribeAsync(CollectorTopics.EngineControl);
                _logger.LogInformation("耳朵已佩戴完毕，等待 UI 下发配置...");
            

            // 保持后台任务运行
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}