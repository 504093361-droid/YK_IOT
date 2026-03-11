// 1. 创建 MQTT 客户端
using Confluent.Kafka;
using Contracts.Models;
using MQTTnet;
using System.Text;
using System.Text.Json;
using System.Buffers;
using MQTTnet.Protocol;
using ScadaEdge.Services;


var mqttFactory = new MqttClientFactory();
using var mqttClient = mqttFactory.CreateMqttClient();

var mqttOptions = new MqttClientOptionsBuilder()
    .WithTcpServer("127.0.0.1", 1883)
    .Build();

await mqttClient.ConnectAsync(mqttOptions, CancellationToken.None);
Console.WriteLine("ScadaEdge connected to MQTT Broker.");

// 2. 创建 Kafka Producer
var producerConfig = new ProducerConfig
{
    BootstrapServers = "127.0.0.1:9092"
};

using var kafkaProducer = new ProducerBuilder<string, string>(producerConfig).Build();
Console.WriteLine("ScadaEdge connected to Kafka.");

// 3. 注册接收原始数据事件
mqttClient.ApplicationMessageReceivedAsync += async e =>
{
    try
    {
        // 读取 MQTT 消息体
        var topic = e.ApplicationMessage.Topic;//先看主题，是哪种类型的消息
        var payloadBytes = e.ApplicationMessage.Payload.ToArray();
        var json = Encoding.UTF8.GetString(payloadBytes);


        if (topic.StartsWith("device/raw/"))
        {
            var rawData = JsonSerializer.Deserialize<RawDeviceMessage>(json);
            if (rawData == null) return;

            Console.WriteLine($"[RAW-IN ] Topic={topic}");
            Console.WriteLine($"         {json}");

            // 清洗、转换、语义化
            var cleanedData = DataProcessor.CleanseAndConvert(rawData);
            var cleanedJson = JsonSerializer.Serialize(cleanedData);

            // 发到 MQTT / UNS
            var mqttMessage = new MqttApplicationMessageBuilder()
                .WithTopic(cleanedData.Namespace)
                .WithPayload(cleanedJson)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await mqttClient.PublishAsync(mqttMessage, CancellationToken.None);
            Console.WriteLine($"[UNS-OUT] {cleanedJson}");

            // 发到 Kafka
            await kafkaProducer.ProduceAsync(
                "uns.telemetry",
                new Message<string, string>
                {
                    Key = cleanedData.DeviceId,
                    Value = cleanedJson
                });

            Console.WriteLine($"[KAFKA  ] {cleanedJson}");
            Console.WriteLine();
            return;
        }

        // =========================
        // B. 命令链：cmd/site1/line1/+
        // =========================
        if (topic.StartsWith("cmd/site1/line1/"))
        {
            var command = JsonSerializer.Deserialize<CommandMessage>(json);
            if (command == null) return;

            Console.WriteLine($"[CMD-IN ] Topic={topic}");
            Console.WriteLine($"         {json}");

            // 模拟命令执行
            var commandResult = CommandProcessor.Handle(command);
            var resultJson = JsonSerializer.Serialize(commandResult);

            // 发布 ACK
            var ackTopic = $"ack/site1/line1/{command.DeviceId}";

            var ackMessage = new MqttApplicationMessageBuilder()
                .WithTopic(ackTopic)
                .WithPayload(resultJson)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await mqttClient.PublishAsync(ackMessage, CancellationToken.None);

            Console.WriteLine($"[ACK-OUT] Topic={ackTopic}");
            Console.WriteLine($"         {resultJson}");
            Console.WriteLine();
            return;
        }

    }
    catch (Exception ex)
    {
        Console.WriteLine($"ScadaEdge error: {ex}");
    }
};

// 4. 订阅原始数据
var subscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
    .WithTopicFilter(f => f.WithTopic("device/raw/#"))
    .Build();

await mqttClient.SubscribeAsync(subscribeOptions, CancellationToken.None);

Console.WriteLine("ScadaEdge running...");
await Task.Delay(Timeout.Infinite);