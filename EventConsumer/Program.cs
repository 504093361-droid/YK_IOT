using System.Buffers;
using System.Text;
using System.Text.Json;
using Contracts;
using Contracts.Models;
using MQTTnet;

Console.OutputEncoding = Encoding.UTF8;

// 2. 创建 MQTT 客户端
var mqttFactory = new MqttClientFactory();
using var mqttClient = mqttFactory.CreateMqttClient();

// 3. 配置 MQTT Broker 连接参数
var mqttOptions = new MqttClientOptionsBuilder()
    .WithTcpServer("127.0.0.1", 1883)
    .Build();

// 4. 连接 Broker
await mqttClient.ConnectAsync(mqttOptions, CancellationToken.None);
Console.WriteLine("EventConsumer connected to MQTT Broker.");

// 5. 注册消息接收事件
mqttClient.ApplicationMessageReceivedAsync += async e =>
{
    try
    {
        var topic = e.ApplicationMessage.Topic;
        var payloadBytes = e.ApplicationMessage.Payload.ToArray();
        var json = Encoding.UTF8.GetString(payloadBytes);

        var eventMessage = JsonSerializer.Deserialize<EventMessage>(json,JsonHelper.DefaultOptions);
        if (eventMessage == null)
        {
            Console.WriteLine("[EVENT-IN] 事件反序列化失败。");
            return;
        }

        Console.WriteLine($"[EVENT-IN ] Topic={topic}");
        Console.WriteLine($"           EventId={eventMessage.EventId}");
        Console.WriteLine($"           DeviceId={eventMessage.DeviceId}");
        Console.WriteLine($"           EventType={eventMessage.EventType}");
        Console.WriteLine($"           Severity={eventMessage.Severity}");
        Console.WriteLine($"           EventName={eventMessage.EventName}");
        Console.WriteLine($"           Message={eventMessage.Message}");
        Console.WriteLine($"           SourceSystem={eventMessage.SourceSystem}");
        Console.WriteLine($"           RelatedCommandId={eventMessage.RelatedCommandId}");
        Console.WriteLine($"           Namespace={eventMessage.Namespace}");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"EventConsumer error: {ex}");
    }

    await Task.CompletedTask;
};

// 6. 订阅事件 Topic
var subscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
    .WithTopicFilter(f => f.WithTopic("event/site1/line1/+"))
    .Build();

await mqttClient.SubscribeAsync(subscribeOptions, CancellationToken.None);

Console.WriteLine("EventConsumer running...");
await Task.Delay(Timeout.Infinite);