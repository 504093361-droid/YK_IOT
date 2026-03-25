
using Contracts.Interface;
using MQTTnet;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Collector.UI.Service
{
    public class MqttService : IMqttService
    {
        private readonly ILogger _logger;

        // 通过构造函数注入日志服务
        public MqttService(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<(bool IsSuccess, string ErrorMessage)> PublishAsync(string topic, string payload, bool retain = false)
        {
            try
            {
                var mqttFactory = new MqttClientFactory();
                using var mqttClient = mqttFactory.CreateMqttClient();

                // 这里的配置后续可以进一步抽离到 appsettings.json 中
                var mqttOptions = new MqttClientOptionsBuilder()
                    .WithTcpServer("127.0.0.1", 1883)
                    //     .WithClientId($"ScadaUI_Pub_{Guid.NewGuid():N}") //UI发布端不常用，可以先留着。 每次生成一个唯一的 ClientId，避免连接冲突 
                    .Build();

                var connectResult = await mqttClient.ConnectAsync(mqttOptions);

                if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
                {
                    return (false, $"连接 Broker 失败: {connectResult.ResultCode}");
                }

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce) // QoS 2
                    .WithRetainFlag(retain)
                    .Build();

                await mqttClient.PublishAsync(message);
                await mqttClient.DisconnectAsync();

                _logger.Information("MQTT 消息推送成功 -> Topic: {Topic}", topic);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "MQTT 消息推送异常 -> Topic: {Topic}", topic);
                return (false, ex.Message);
            }
        }
    }
}
