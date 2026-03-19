// 1. 设置控制台输出编码，避免中文显示异常
using Confluent.Kafka;
using System.Text;
using System.Text.Json;
using Contracts;
using Contracts.Models;
Console.OutputEncoding = Encoding.UTF8;

// 2. Kafka Consumer 配置
var config = new ConsumerConfig
{
    BootstrapServers = "127.0.0.1:9092",
    GroupId = "uns-demo-event-consumer-group",
    AutoOffsetReset = AutoOffsetReset.Earliest,
    EnableAutoCommit = true
};

// 3. 创建 Consumer
using var consumer = new ConsumerBuilder<string, string>(config).Build();

// 4. 订阅事件 Topic
consumer.Subscribe("uns.event");

Console.WriteLine("KafkaEventConsumer connected to Kafka.");
Console.WriteLine("KafkaEventConsumer running...");

try
{
    while (true)
    {
        // 5. 阻塞式消费消息
        var consumeResult = consumer.Consume();

        if (consumeResult == null)
            continue;

        var messageKey = consumeResult.Message.Key;
        var messageValue = consumeResult.Message.Value;

        Console.WriteLine($"[KAFKA-EV ] Partition={consumeResult.Partition}, Offset={consumeResult.Offset}");

        // 6. 反序列化事件消息
        var eventMessage = JsonSerializer.Deserialize<EventMessage>(messageValue);

        if (eventMessage == null)
        {
            Console.WriteLine("[KAFKA-EV ] 事件反序列化失败。");
            Console.WriteLine();
            continue;
        }

        // 7. 打印事件内容
        Console.WriteLine($"[EVENT    ] EventId={eventMessage.EventId}");
        Console.WriteLine($"[EVENT    ] DeviceId={eventMessage.DeviceId}");
        Console.WriteLine($"[EVENT    ] EventType={eventMessage.EventType}");
        Console.WriteLine($"[EVENT    ] Severity={eventMessage.Severity}");
        Console.WriteLine($"[EVENT    ] EventName={eventMessage.EventName}");
        Console.WriteLine($"[EVENT    ] Message={eventMessage.Message}");
        Console.WriteLine($"[EVENT    ] SourceSystem={eventMessage.SourceSystem}");
        Console.WriteLine($"[EVENT    ] RelatedCommandId={eventMessage.RelatedCommandId}");
        Console.WriteLine($"[EVENT    ] Namespace={eventMessage.Namespace}");
        Console.WriteLine();
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("KafkaEventConsumer canceled.");
}
catch (ConsumeException ex)
{
    Console.WriteLine($"Kafka event consume error: {ex.Error.Reason}");
}
catch (Exception ex)
{
    Console.WriteLine($"KafkaEventConsumer error: {ex}");
}
finally
{
    consumer.Close();
}