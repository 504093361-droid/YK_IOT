using Contracts.Interface;
using MQTTnet;

using Serilog;
using System;
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

        // 🟢 1. 实现接口中的事件
        public event Func<string, string, Task> OnMessageReceived;

        public MqttService(ILogger logger)
        {
            _logger = logger;

            var factory = new MqttClientFactory();
            _mqttClient = factory.CreateMqttClient();

            // 🟢 铁律：UI 端随机 ClientId，CleanSession=true (只看当下，不看历史积压)
            _options = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", 1883)
                .WithClientId($"ScadaUI_Viewer_{Guid.NewGuid():N}")
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .Build();

            // 注册原生接收事件，抛给 ViewModel
            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                string topic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                if (OnMessageReceived != null)
                {
                    await OnMessageReceived.Invoke(topic, payload);
                }
            };

            // 断线自动重连
            _mqttClient.DisconnectedAsync += async e =>
            {
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
                    _logger.Information("✅ UI 端 MQTT 成功连接至 Broker，准备就绪！");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "UI 端 MQTT 连接失败，后台将持续重试。");
                }
            }
        }

        // 🟢 2. 重构发布方法 (复用长连接，千万别再 DisconnectAsync 了！)
        public async Task<(bool IsSuccess, string ErrorMessage)> PublishAsync(string topic, string payload, bool retain = false)
        {
            try
            {
                // 没连上的话先连
                if (!_mqttClient.IsConnected) await ConnectAsync();

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

        // 🟢 3. 实现接口中的订阅方法
        public async Task SubscribeAsync(string topic)
        {
            try
            {
                // 没连上的话先连
                if (!_mqttClient.IsConnected) await ConnectAsync();

                var subOptions = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(topic))
                    .Build();

                await _mqttClient.SubscribeAsync(subOptions);
                _logger.Information("UI 已成功订阅主题 -> {Topic}", topic);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "UI 订阅主题失败 -> {Topic}", topic);
            }
        }
    }
}