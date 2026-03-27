using Collector.Contracts.Topics;
using Contracts.Interface; // 你的全局契约命名空间
using Microsoft.Extensions.Logging;
using MQTTnet;

using System;
using System.Collections.Generic;
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

        // 🟢 核心利器：记忆小本本！记录所有需要订阅的主题
        private readonly HashSet<string> _subscribedTopics = new HashSet<string>();

        // 暴露一个事件，让 Receiver (耳朵) 能够听到消息
        public event Func<string, string, Task> OnMessageReceived;

        // 🟢 1. 契约履行：实现接口要求的大喇叭（连接状态变化事件）
        public event Action<bool> OnConnectionStatusChanged;

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

            // 收到消息时的处理
            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                string topic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                if (OnMessageReceived != null)
                {
                    await OnMessageReceived.Invoke(topic, payload);
                }
            };

            // 每次连接成功时（包括首次和断线重连）
            _mqttClient.ConnectedAsync += async e =>
            {
                // 🟢 2. 大喊一声：我连上了！
                OnConnectionStatusChanged?.Invoke(true);


                _logger.LogInformation("✅ 边缘端 MQTT 成功连接到 Broker！开始恢复订阅树...");
                foreach (var topic in _subscribedTopics)
                {
                    try
                    {
                        var subOptions = new MqttClientSubscribeOptionsBuilder().WithTopicFilter(f => f.WithTopic(topic)).Build();
                        await _mqttClient.SubscribeAsync(subOptions);
                        _logger.LogInformation("  -> 成功恢复订阅: {Topic}", topic);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "恢复订阅主题 {Topic} 失败", topic);
                    }
                }
            };

            // 注册断线重连机制
            _mqttClient.DisconnectedAsync += async e =>
            {
                // 🟢 3. 大喊一声：我断开了！
                OnConnectionStatusChanged?.Invoke(false);

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
                    // 注意：连接成功的日志现在移到了 ConnectedAsync 事件里去打印了
                }
                catch (Exception)
                {
                    // 故意只用 Debug 级别，或者简单警告，因为断线重试机制会疯狂触发这里，没必要满屏报红
                    _logger.LogWarning("MQTT Broker 尚未启动或网络异常，等待重连机制接管...");
                }
            }
        }

        // 重构订阅逻辑：不再强求立刻成功，而是“先记账”
        public async Task SubscribeAsync(string topic)
        {
            // 1. 先记在小本本上（HashSet 会自动去重）
            _subscribedTopics.Add(topic);

            // 2. 如果当前碰巧连着，就顺手立刻订阅一下；如果没连着，等重连成功后 ConnectedAsync 会自动代劳！
            if (_mqttClient.IsConnected)
            {
                try
                {
                    var subOptions = new MqttClientSubscribeOptionsBuilder().WithTopicFilter(f => f.WithTopic(topic)).Build();
                    await _mqttClient.SubscribeAsync(subOptions);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "立刻订阅主题 {Topic} 时发生异常！", topic);
                }
            }
            else
            {
                // 如果没连上，触发一下连接动作（它可能失败，但没关系）
                await ConnectAsync();
            }
        }

        // 发布方法保持原有的 try-catch 安全网，但也做了二次检查
        public async Task<(bool IsSuccess, string ErrorMessage)> PublishAsync(string topic, string payload, bool retain = false)
        {
            try
            {
                if (!_mqttClient.IsConnected) await ConnectAsync();

                // 二次检查：如果连接方法执行完还是没连上，优雅退回，不要抛异常闪退
                if (!_mqttClient.IsConnected)
                {
                    return (false, "MQTT 尚未连接，消息丢弃或等待下一周期。");
                }

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
                _logger.LogWarning("发布消息失败 (可能是瞬间断线) -> Topic: {Topic}, Error: {Message}", topic, ex.Message);
                return (false, ex.Message);
            }
        }
    }
}