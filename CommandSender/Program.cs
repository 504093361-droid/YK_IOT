// 1. 创建 MQTT 客户端
using Contracts.Models;
using MQTTnet;
using MQTTnet.Protocol;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Buffers;

using CommandType = Contracts.Models.CommandType;

var mqttFactory = new MqttClientFactory();
using var mqttClient = mqttFactory.CreateMqttClient();

var mqttOptions = new MqttClientOptionsBuilder()
    .WithTcpServer("127.0.0.1", 1883)
    .Build();

await mqttClient.ConnectAsync(mqttOptions, CancellationToken.None);
Console.WriteLine("CommandSender connected to MQTT Broker.");

string deviceId = "mixer01";
string commandTopic = $"cmd/site1/line1/{deviceId}";
string ackTopic = $"ack/site1/line1/{deviceId}";

// 2. 订阅 ACK Topic
mqttClient.ApplicationMessageReceivedAsync += async e =>
{
    try
    {
        var topic = e.ApplicationMessage.Topic;
        var payloadBytes = e.ApplicationMessage.Payload.ToArray();
        var json = Encoding.UTF8.GetString(payloadBytes);

        if (topic == ackTopic)
        {
            var result = JsonSerializer.Deserialize<CommandResultMessage>(json);
            if (result == null) return;

            Console.WriteLine($"[ACK-IN ] Topic={topic}");
            Console.WriteLine($"         CommandId={result.CommandId}");
            Console.WriteLine($"         DeviceId={result.DeviceId}");
            Console.WriteLine($"         Status={result.Status}");
            Console.WriteLine($"         Message={result.Message}");
            Console.WriteLine();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"CommandSender error: {ex}");
    }

    await Task.CompletedTask;
};

var ackSubscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
    .WithTopicFilter(f => f.WithTopic(ackTopic))
    .Build();

await mqttClient.SubscribeAsync(ackSubscribeOptions, CancellationToken.None);

Console.WriteLine("CommandSender running...");
Console.WriteLine("按 1 发送 SetParameter 命令");
Console.WriteLine("按 2 发送 Start 命令");
Console.WriteLine("按 3 发送 Stop 命令");
Console.WriteLine("按 4 发送 Reset 命令");
Console.WriteLine("按 Q 退出");
Console.WriteLine();

while (true)
{
    var key = Console.ReadKey(true).Key;

    if (key == ConsoleKey.Q)
    {
        break;
    }

    CommandMessage? command = key switch
    {
        ConsoleKey.D1 or ConsoleKey.NumPad1 => new CommandMessage
        {
            CommandId = Guid.NewGuid().ToString("N"),
            DeviceId = deviceId,
            CommandType = CommandType.SetParameter,
            ParameterName = "TemperatureSetpoint",
            ParameterValue = "85",
            SourceSystem = "CommandSender",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TimeoutMs = 5000
        },

        ConsoleKey.D2 or ConsoleKey.NumPad2 => new CommandMessage
        {
            CommandId = Guid.NewGuid().ToString("N"),
            DeviceId = deviceId,
            CommandType = CommandType.Start,
            SourceSystem = "CommandSender",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TimeoutMs = 5000
        },

        ConsoleKey.D3 or ConsoleKey.NumPad3 => new CommandMessage
        {
            CommandId = Guid.NewGuid().ToString("N"),
            DeviceId = deviceId,
            CommandType = CommandType.Stop,
            SourceSystem = "CommandSender",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TimeoutMs = 5000
        },

        ConsoleKey.D4 or ConsoleKey.NumPad4 => new CommandMessage
        {
            CommandId = Guid.NewGuid().ToString("N"),
            DeviceId = deviceId,
            CommandType = CommandType.Reset,
            SourceSystem = "CommandSender",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TimeoutMs = 5000
        },

        _ => null
    };

    if (command == null)
    {
        continue;
    }

    var json = JsonSerializer.Serialize(command);

    var mqttMessage = new MqttApplicationMessageBuilder()
        .WithTopic(commandTopic)
        .WithPayload(json)
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
        .Build();

    await mqttClient.PublishAsync(mqttMessage, CancellationToken.None);

    Console.WriteLine($"[CMD-OUT] Topic={commandTopic}");
    Console.WriteLine($"         {json}");
    Console.WriteLine();
}
    