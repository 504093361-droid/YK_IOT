// 1. 创建 MQTT 客户端
using Confluent.Kafka;
using Contracts.Models;
using MQTTnet;
using System.Text;
using System.Text.Json;
using System.Buffers;
using MQTTnet.Protocol;
using ScadaEdge.Services;
using Contracts;


var mqttFactory = new MqttClientFactory();
using var mqttClient = mqttFactory.CreateMqttClient();

var mqttOptions = new MqttClientOptionsBuilder()
    .WithTcpServer("127.0.0.1", 1883)
    .Build();

await mqttClient.ConnectAsync(mqttOptions, CancellationToken.None);
Console.WriteLine("ScadaEdge connected to MQTT Broker.");

var producerConfig = new ProducerConfig
{
    BootstrapServers = "127.0.0.1:9092"
};

using var kafkaProducer = new ProducerBuilder<string, string>(producerConfig).Build();
Console.WriteLine("ScadaEdge connected to Kafka.");

mqttClient.ApplicationMessageReceivedAsync += async e =>
{
    try
    {
        var topic = e.ApplicationMessage.Topic;
        Console.WriteLine($"[MQTT-Received ] Topic={topic}");

        var payloadBytes = e.ApplicationMessage.Payload.ToArray();
        var json = Encoding.UTF8.GetString(payloadBytes);

        // =========================
        // A. 原始数据链：device/raw/#
        // =========================
        if (topic.StartsWith("device/raw/"))
        {
            var rawData = JsonSerializer.Deserialize<RawDeviceMessage>(json);
            if (rawData == null) return;

            Console.WriteLine($"[RAW-Received  ] {json}");

            // 1. 清洗、转换、语义化
            var cleanedData = DataProcessor.CleanseAndConvert(rawData);
            var cleanedJson = JsonSerializer.Serialize(cleanedData,JsonHelper.DefaultOptions);

            // 2. 发 Telemetry 到 MQTT/UNS
            var telemetryMqttMessage = new MqttApplicationMessageBuilder()
                .WithTopic(cleanedData.Namespace)
                .WithPayload(cleanedJson)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await mqttClient.PublishAsync(telemetryMqttMessage, CancellationToken.None);
            Console.WriteLine($"[UNS-BroadCast ] {cleanedJson}");

            // 3. 发 Telemetry 到 Kafka
            await kafkaProducer.ProduceAsync(
                "uns.telemetry",
                new Message<string, string>
                {
                    Key = cleanedData.DeviceId,
                    Value = cleanedJson
                });

            Console.WriteLine($"[KAFKA-BroadCast ] {cleanedJson}");

            // 4. 尝试生成事件
            var telemetryEvent = DataProcessor.TryCreateTelemetryEvent(rawData, cleanedData);
            if (telemetryEvent != null)
            {
                var eventJson = JsonSerializer.Serialize(telemetryEvent,JsonHelper.DefaultOptions);

                // 发到 MQTT Event Topic
                var eventMqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(telemetryEvent.Namespace)
                    .WithPayload(eventJson)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await mqttClient.PublishAsync(eventMqttMessage, CancellationToken.None);
                Console.WriteLine($"[UNS-E-BroadCast] {eventJson}");

                // 发到 Kafka Event Topic
                await kafkaProducer.ProduceAsync(
                    "uns.event",
                    new Message<string, string>
                    {
                        Key = telemetryEvent.DeviceId,
                        Value = eventJson
                    });

                Console.WriteLine($"[KAFKA-E-BroadCast ] {eventJson}");
            }

            Console.WriteLine();
            return;
        }

        // =========================
        // B. 命令链：cmd/#
        // =========================
        if (topic.StartsWith("cmd/"))
        {
            var command = JsonSerializer.Deserialize<CommandMessage>(json);
            if (command == null) return;

            Console.WriteLine($"[CMD-Received  ] {json}");

            // 1. 命令处理
            var commandResult = CommandProcessor.Handle(command);
            var resultJson = JsonSerializer.Serialize(commandResult,JsonHelper.DefaultOptions);

            // 2. 发布 ACK
            var ackTopic = $"ack/site1/line1/{command.DeviceId}";

            var ackMessage = new MqttApplicationMessageBuilder()
                .WithTopic(ackTopic)
                .WithPayload(resultJson)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await mqttClient.PublishAsync(ackMessage, CancellationToken.None);
            Console.WriteLine($"[ACK-BroadCast ] Topic={ackTopic}");
            Console.WriteLine($"          {resultJson}");

            // 3. 生成命令事件
            var commandEvent = CommandProcessor.CreateCommandEvent(command, commandResult);
            var commandEventJson = JsonSerializer.Serialize(commandEvent, JsonHelper.DefaultOptions);

            // 发到 MQTT Event Topic
            var commandEventMqttMessage = new MqttApplicationMessageBuilder()
                .WithTopic(commandEvent.Namespace)
                .WithPayload(commandEventJson)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await mqttClient.PublishAsync(commandEventMqttMessage, CancellationToken.None);
            Console.WriteLine($"[UNS-Event] {commandEventJson}");

            // 发到 Kafka Event Topic
            await kafkaProducer.ProduceAsync(
                "uns.event",
                new Message<string, string>
                {
                    Key = commandEvent.DeviceId,
                    Value = commandEventJson
                });

            Console.WriteLine($"[KAFKA-Event ] {commandEventJson}");
            Console.WriteLine();
            return;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ScadaEdge error: {ex}");
    }
};

// 订阅 raw
var rawSubscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
    .WithTopicFilter(f => f.WithTopic("device/raw/#"))
    .Build();

await mqttClient.SubscribeAsync(rawSubscribeOptions, CancellationToken.None);

// 订阅 cmd
var cmdSubscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
    .WithTopicFilter(f => f.WithTopic("cmd/#"))
    .Build();

await mqttClient.SubscribeAsync(cmdSubscribeOptions, CancellationToken.None);

Console.WriteLine("ScadaEdge running...");
await Task.Delay(Timeout.Infinite);