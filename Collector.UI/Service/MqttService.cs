using Contracts.Interface;
using MQTTnet;

using Serilog;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Collector.UI.Service
{
    // UI端专属的 MQTT 服务 (保持长连接，用于发布指令和持续监听状态)
    public class MqttService : IMqttService
    {
        private readonly ILogger _logger;
        private readonly IMqttClient _mqttClient;
        private readonly MqttClientOptions _options;

        // 🟢 核心利器：UI端的记忆小本本！记录所有需要监听的主题
        private readonly HashSet<string> _subscribedTopics = new HashSet<string>();
        // 1. 在类里加上这个事件的实现：
        public event Action<bool> OnConnectionStatusChanged;
        // 1. 实现接口中的事件
        public event Func<string, string, Task> OnMessageReceived;

        public MqttService(ILogger logger)
        {
            _logger = logger;

            var factory = new MqttClientFactory();
            _mqttClient = factory.CreateMqttClient();

            // 铁律：UI 端随机 ClientId，CleanSession=true (只看当下，不看历史积压)
            _options = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", 1883)
                .WithClientId($"ScadaUI_Viewer_{Guid.NewGuid():N}")
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .Build();

            // 收到消息事件
            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                string topic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                if (OnMessageReceived != null)
                {
                    await OnMessageReceived.Invoke(topic, payload);
                }
            };

            // 🟢 核心大招：每次连接成功时（首次或断线重连），自动翻开小本本重新订阅！
            _mqttClient.ConnectedAsync += async e =>
            {
                OnConnectionStatusChanged?.Invoke(true); // 🟢 通知外面：我连上了！
                _logger.Information("✅ UI 端 MQTT 成功连接至 Broker，准备恢复监听树...");

             

      


                foreach (var topic in _subscribedTopics)
                {
                    try
                    {
                        var subOptions = new MqttClientSubscribeOptionsBuilder().WithTopicFilter(f => f.WithTopic(topic)).Build();
                        await _mqttClient.SubscribeAsync(subOptions);
                        _logger.Information("  -> 成功恢复监听主题: {Topic}", topic);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "恢复监听主题 {Topic} 失败", topic);
                    }
                }
            };

            // 断线自动重连
            _mqttClient.DisconnectedAsync += async e =>
            {
                OnConnectionStatusChanged?.Invoke(false); // 🟢 通知外面：我断开了！
                _logger.Warning("UI 端 MQTT 已断开连接！原因: {Reason}。5秒后尝试重连...", e.Reason);
                await Task.Delay(5000);
                await ConnectAsync();
            };
        }

        // 内部维护长连接的方法
        private async Task ConnectAsync()
        {
            if (!_mqttClient.IsConnected)
            {
                try
                {
                    await _mqttClient.ConnectAsync(_options);
                    // 成功的日志在 ConnectedAsync 事件里打印了
                }
                catch (Exception)
                {
                    _logger.Warning("UI 端 MQTT 连接失败 (Broker可能未启动)，后台重连机制持续待命中...");
                }
            }
        }

        // 🟢 2. 重构发布方法 (增加二次检查，防止 Broker 没开时强行 Publish 报错)
        public async Task<(bool IsSuccess, string ErrorMessage)> PublishAsync(string topic, string payload, bool retain = false)
        {
            try
            {
                // 没连上的话先连
                if (!_mqttClient.IsConnected) await ConnectAsync();

                // 🟢 二次防御：如果 ConnectAsync 走完还是没连上（比如没开EMQX），直接优雅退回，别抛异常
                if (!_mqttClient.IsConnected)
                {
                    return (false, "MQTT 尚未连接，发送失败。");
                }

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce) // QoS 1 够用了
                    .WithRetainFlag(retain)
                    .Build();

                await _mqttClient.PublishAsync(message);

                _logger.Information("MQTT 消息推送成功 -> Topic: {Topic}", topic);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "MQTT 消息推送异常 -> Topic: {Topic}", topic);
                return (false, ex.Message);
            }
        }

        // 🟢 3. 重构订阅方法 (先记账，后干活)
        public async Task SubscribeAsync(string topic)
        {
            // 1. 先记在小本本上，保证不管什么时候连上都不会忘记！
            _subscribedTopics.Add(topic);

            // 2. 如果当前正好连着，就顺手定阅一下；没连着拉倒，等 ConnectedAsync 帮我们定阅
            if (_mqttClient.IsConnected)
            {
                try
                {
                    var subOptions = new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(topic))
                        .Build();

                    await _mqttClient.SubscribeAsync(subOptions);
                    _logger.Information("UI 已立即订阅主题 -> {Topic}", topic);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "UI 立即订阅主题失败 -> {Topic}", topic);
                }
            }
            else
            {
                await ConnectAsync();
            }
        }
    }
}