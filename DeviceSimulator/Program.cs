// 1. 创建 MQTT 客户端工厂
using Contracts.Models;
using System.Text.Json;
using MQTTnet;
using Contracts;

var mqttFactory = new MqttClientFactory();

// 2. 创建 MQTT 客户端
using var mqttClient = mqttFactory.CreateMqttClient();

// 3. 配置 MQTT 连接参数
var mqttOptions = new MqttClientOptionsBuilder()
    .WithTcpServer("127.0.0.1", 1883)
    .Build();

// 4. 连接到 Mosquitto Broker
await mqttClient.ConnectAsync(mqttOptions, CancellationToken.None);
Console.WriteLine("DeviceSimulator connected to MQTT Broker.");

// 5. 模拟设备编号
string deviceId = "mixer01";

// 6. 模拟随机数，用来生成测试数据
var random = new Random();


Console.Title = "DeviceSim";
Console.WriteLine("DeviceSimulator running...");

while (true)
{
    // 7. 构造一条原始设备数据
    // 温度这里故意偶尔生成异常值，便于测试 ScadaEdge 的清洗逻辑
    var rawData = new RawDeviceMessage
    {
        DeviceId = deviceId,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Temperature = GenerateTemperature(random),
        Pressure = Math.Round(1.0 + random.NextDouble() * 0.5, 2)
    };

    // 8. 序列化为 JSON
    var payload = JsonSerializer.Serialize(rawData,JsonHelper.DefaultOptions);

    // 9. 构建 MQTT 消息
    var mqttMessage = new MqttApplicationMessageBuilder()
        .WithTopic($"device/raw/{deviceId}")
        .WithPayload(payload)
        .Build();

    // 10. 发布到原始数据 Topic
    await mqttClient.PublishAsync(mqttMessage, CancellationToken.None);

    Console.WriteLine($"[RAW-BroadCast] {payload}");

    // 11. 每秒发送一次
    await Task.Delay(3000);
}



double GenerateTemperature(Random random)
{
    // 10% 概率生成异常值
    if (random.Next(1, 11) > 7)
    {
        return random.Next(-200, 500);
    }

    // 正常值范围 60~90
    return Math.Round(60 + random.NextDouble() * 30, 2);
}