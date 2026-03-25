using Collector.Contracts;

using Collector.Contracts.Model;
using Collector.Contracts.Topics;
using Microsoft.Extensions.Logging;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Collector.Edge.Publishing
{
    public interface IMqttPublisher
    {
        Task PublishRawDataAsync(RawMessage message);
    }

    public class MqttPublisher : IMqttPublisher
    {
        private readonly ILogger<MqttPublisher> _logger;

        public MqttPublisher(ILogger<MqttPublisher> logger)
        {
            _logger = logger;
        }

        public async Task PublishRawDataAsync(RawMessage message)
        {
            try
            {
                // 使用 Contracts 中约定的主题规则
                string topic = CollectorTopics.GetDeviceRawDataTopic(message.DeviceId);
                string payload = JsonSerializer.Serialize(message);

                if (message.IsSuccess)
                {
                    _logger.LogInformation("[发布] 成功 | 点位: {Addr} | 值: {Val}", message.Address, message.Value);
                }
                else
                {
                    _logger.LogWarning("[发布] 失败 | 点位: {Addr} | 错误: {Err}", message.Address, message.ErrorMessage);
                }

                // TODO: 在这里注入 IMqttService 并调用 _mqttService.PublishAsync(topic, payload)

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发布 RawMessage 时发生意外异常");
            }
        }
    }
}