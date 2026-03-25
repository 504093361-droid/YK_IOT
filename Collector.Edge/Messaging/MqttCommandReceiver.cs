using Collector.Contracts.Topics;
using Collector.Edge.Control;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Collector.Edge.Messaging
{
    public class MqttCommandReceiver : BackgroundService
    {
        private readonly IEdgeController _controller;
        private readonly ILogger<MqttCommandReceiver> _logger;
        private IMqttClient _mqttClient;

        // 注入 Control 大脑
        public MqttCommandReceiver(IEdgeController controller, ILogger<MqttCommandReceiver> logger)
        {
            _controller = controller;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var factory = new MqttClientFactory();
            _mqttClient = factory.CreateMqttClient();

            // 1. 注册消息接收事件
            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                string topic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);


           

                // 用最原始的 Console 打印，防止 Logger 级别被屏蔽
                Console.WriteLine($"\n[超级调试] 收到来自 Broker 的消息！");
                Console.WriteLine($"[超级调试] 主题: {topic}");
                Console.WriteLine($"[超级调试] 载荷: {payload} 字符\n");


                // 判断是否为配置下发主题 (如果用的是 Contracts 里的常量，这里直接替换)
                if (topic == CollectorTopics.ConfigUpdate)
                {
                    _logger.LogInformation("耳朵 (Messaging) 听到配置更新主题消息。");
                    // 甩给大脑处理，耳朵不负责解析
                    await _controller.HandleConfigUpdatedAsync(payload);
                }
            };

            // 2. 配置连接选项 (注意 Edge 端的 ClientId 应该是固定的)
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", 1883)
                .WithClientId("ScadaEdge_MainNode_01")
                .WithCleanSession(false) // 工业端推荐 false，结合 Qos 保留离线消息
                .Build();

            // 3. 连接并订阅
            try
            {
                await _mqttClient.ConnectAsync(options, stoppingToken);
                _logger.LogInformation("Edge 成功连接至 MQTT Broker！");

                var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(CollectorTopics.ConfigUpdate))
                    .Build();

                await _mqttClient.SubscribeAsync(subscribeOptions, stoppingToken);
                _logger.LogInformation("Edge 已订阅配置更新主题，等待 UI 下发...");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Edge 连接 MQTT Broker 失败！");
            }

            // 4. 保持后台任务不死
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }

            // 优雅退出
            if (_mqttClient.IsConnected)
            {
                await _mqttClient.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None);
            }
        }
    }
}