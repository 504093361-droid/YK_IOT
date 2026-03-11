// 1. Kafka Consumer 配置
// BootstrapServers: Kafka 地址
// GroupId: 消费者组名称，同一组内会分摊消费
// AutoOffsetReset: 如果没有已提交位点，从哪里开始消费
using Confluent.Kafka;
using Contracts.Models;
using System.Text.Json;

var config = new ConsumerConfig
{
    BootstrapServers = "127.0.0.1:9092",
    GroupId = "uns-demo-consumer-group",
    AutoOffsetReset = AutoOffsetReset.Earliest,
    EnableAutoCommit = true
};

// 2. 创建 Consumer
using var consumer = new ConsumerBuilder<string, string>(config).Build();

// 3. 订阅 Topic
consumer.Subscribe("uns.telemetry");

Console.Title= "🔴KafkaConsumer";
Console.WriteLine("KafkaConsumer connected to Kafka.");
Console.WriteLine("KafkaConsumer running...");

try
{
    while (true)
    {
        // 4. 阻塞式消费消息
        var consumeResult = consumer.Consume();

        if (consumeResult == null)
            continue;

        // 5. 原始 Kafka 消息
        var messageKey = consumeResult.Message.Key;
        var messageValue = consumeResult.Message.Value;

        Console.WriteLine($"[KAFKA-IN ] Partition={consumeResult.Partition}, Offset={consumeResult.Offset}");

        // 6. 反序列化为标准遥测数据
        var telemetry = JsonSerializer.Deserialize<TelemetryMessage>(messageValue);

        if (telemetry == null)
        {
            Console.WriteLine("[KAFKA-IN ] 消息反序列化失败。");
            continue;
        }

        // 7. 模拟下游平台消费
        Console.WriteLine($"[DATA     ] Device={telemetry.DeviceId}, Value={telemetry.Value}, Unit={telemetry.Unit}, Quality={telemetry.Quality}");
        Console.WriteLine($"[NAMESPACE] {telemetry.Namespace}");
        Console.WriteLine();
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("KafkaConsumer canceled.");
}
catch (ConsumeException ex)
{
    Console.WriteLine($"Kafka consume error: {ex.Error.Reason}");
}
catch (Exception ex)
{
    Console.WriteLine($"KafkaConsumer error: {ex}");
}
finally
{
    // 8. 优雅关闭 Consumer
    consumer.Close();
}