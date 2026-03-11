// 1. 创建 MQTT 客户端工厂
using Contracts.Models;
using MQTTnet;
using System.Text;
using System.Text.Json;
using System.Buffers;

var mqttFactory = new MqttClientFactory();

// 2. 创建 MQTT 客户端
using var mqttClient = mqttFactory.CreateMqttClient();

// 3. 配置 MQTT 连接参数
var mqttOptions = new MqttClientOptionsBuilder()
    .WithTcpServer("127.0.0.1", 1883)
    .Build();

// 4. 连接到 Mosquitto Broker
await mqttClient.ConnectAsync(mqttOptions, CancellationToken.None);
Console.WriteLine("MesConsumer connected to MQTT Broker.");

// 5. 注册消息接收事件
mqttClient.ApplicationMessageReceivedAsync += async e =>
{
    try
    {
        // 6. 读取消息体
        var payloadBytes = e.ApplicationMessage.Payload.ToArray();
        var json = Encoding.UTF8.GetString(payloadBytes);

        // 7. 反序列化为标准遥测数据
        var telemetry = JsonSerializer.Deserialize<TelemetryMessage>(json);
        if (telemetry == null) return;

        // 8. 模拟 MES 对数据的消费
        Console.Title = "MESConsumer";
        Console.WriteLine($"[MES-IN] Topic={e.ApplicationMessage.Topic}");
        Console.WriteLine($"         Device={telemetry.DeviceId}, Value={telemetry.Value}, Unit={telemetry.Unit}, Quality={telemetry.Quality}");

        // 9. 这里先不做复杂业务逻辑
        // 后续你可以扩展：
        // - 更新工单状态
        // - 触发质量判断
        // - 写入生产记录
        // - 生成报警事件
    }
    catch (Exception ex)
    {
        Console.WriteLine($"MesConsumer error: {ex.Message}");
    }

    await Task.CompletedTask;
};

// 10. 订阅 UNS 标准 Topic
// + 表示匹配一层设备名，例如 mixer01
var subscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
    .WithTopicFilter(f => f.WithTopic("uns/site1/line1/+/temperature"))
    .Build();

await mqttClient.SubscribeAsync(subscribeOptions, CancellationToken.None);

Console.WriteLine("MesConsumer running...");
await Task.Delay(Timeout.Infinite);