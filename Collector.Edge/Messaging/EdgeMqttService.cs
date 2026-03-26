using Contracts.Interface; // 你的全局契约命名空间
using Microsoft.Extensions.Logging;
using MQTTnet;

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Collector.Edge.Messaging
{
    // 全局唯一的 MQTT 通信大管家 (单例)
    public class EdgeMqttService : IMqttService
    {
        private readonly IMqttClient _mqttClient;
        private readonly MqttClientOptions _options;
        private readonly ILogger<EdgeMqttService> _logger;

        // 暴露一个事件，让 Receiver (耳朵) 能够听到消息
        public event Func<string, string, Task> OnMessageReceived;

        public EdgeMqttService(ILogger<EdgeMqttService> logger)
        {
            _logger = logger;
            var factory = new MqttClientFactory();
            _mqttClient = factory.CreateMqttClient();

            // 铁律：Edge 端必须固定 ClientId
            _options = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", 1883)
                .WithClientId("ScadaEdge_MainNode_01")
                .WithCleanSession(false)
                .Build();

            // 1. 注册原生接收事件，并向外抛出我们自定义的事件
            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                string topic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                if (OnMessageReceived != null)
                {
                    await OnMessageReceived.Invoke(topic, payload);
                }
            };

            // 2. 注册断线重连机制
            _mqttClient.DisconnectedAsync += async e =>
            {
                _logger.LogWarning("边缘端 MQTT 已断线！原因: {Reason}。5秒后重试...", e.Reason);
                await Task.Delay(5000);
                await ConnectAsync();
            };
        }

        // 初始化连接
        public async Task ConnectAsync()
        {
            if (!_mqttClient.IsConnected)
            {
                try
                {
                    await _mqttClient.ConnectAsync(_options);
                    _logger.LogInformation("✅ 边缘端 MQTT (单例大管家) 成功连接 Broker！");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MQTT 连接失败，重试机制将接管。");
                }
            }
        }

        // 订阅方法
        public async Task SubscribeAsync(string topic)
        {
            if (!_mqttClient.IsConnected) await ConnectAsync();
            var subOptions = new MqttClientSubscribeOptionsBuilder().WithTopicFilter(f => f.WithTopic(topic)).Build();
            await _mqttClient.SubscribeAsync(subOptions);
        }

        // 🟢 实现你的 IMqttService 接口 (注意元组返回值)
        public async Task<(bool IsSuccess, string ErrorMessage)> PublishAsync(string topic, string payload, bool retain = false)
        {
            try
            {
                if (!_mqttClient.IsConnected) await ConnectAsync();

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(retain)
                    .Build();

                await _mqttClient.PublishAsync(message);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发布消息失败 -> Topic: {Topic}", topic);
                return (false, ex.Message);
            }
        }
    }
}