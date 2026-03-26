using Collector.Contracts;
using Collector.Contracts.Model;
using Collector.Contracts.Topics;
using Contracts.Interface; // 引入 IMqttService
using Microsoft.Extensions.Logging;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Collector.Edge.Publishing
{
    public interface IMqttPublisher
    {
        Task PublishRawDataAsync(RawMessage message);
        Task PublishDeviceStatusAsync(string deviceId, string status,int statuscode);
    }

    public class MqttPublisher : IMqttPublisher
    {
        private readonly ILogger<MqttPublisher> _logger;
        private readonly IMqttService _mqttService; // 注入司令部

        public MqttPublisher(ILogger<MqttPublisher> logger, IMqttService mqttService)
        {
            _logger = logger;
            _mqttService = mqttService;
        }

        public async Task PublishRawDataAsync(RawMessage message)
        {
            try
            {
                string topic = CollectorTopics.GetDeviceRawDataTopic(message.DeviceId);
                string payload = JsonSerializer.Serialize(message);

                // 🟢 真正调用发布，并获取返回值
                var result = await _mqttService.PublishAsync(topic, payload, retain: false);

                if (!result.IsSuccess)
                {
                    _logger.LogWarning("[发布底层失败] 点位: {Addr} | 错误: {Err}", message.Address, result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发布 RawMessage 时发生代码异常");
            }
        }

        public async Task PublishDeviceStatusAsync(string deviceId, string status,int statuscode)
        {
            try
            {
                string topic = CollectorTopics.GetDeviceStatusTopic(deviceId);
                // 🟢 JSON 里多加了一个 StatusCode 字段
                string payload = $"{{\"DeviceId\":\"{deviceId}\", \"Status\":\"{status}\", \"StatusCode\":{statuscode}}}";

                // 🟢 发布状态心跳 (retain: true)
                var result = await _mqttService.PublishAsync(topic, payload, retain: true);

                if (result.IsSuccess)
                {
                    _logger.LogInformation("📢 [状态更新] 设备: {Id} -> {Status}", deviceId, status);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发布设备 [{DeviceId}] 状态时发生异常", deviceId);
            }
        }
    }
}