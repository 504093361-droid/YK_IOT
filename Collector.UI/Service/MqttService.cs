using Contracts.Interface;
using MQTTnet;
using Serilog;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Collector.UI.Service
{
    /// <summary>
    /// UI端专属 MQTT 服务
    /// 核心设计：
    /// 1. MQTT 收到消息 -> 原始消息先入后台队列
    /// 2. 后台线程把消息合并进“最新状态缓存”
    /// 3. UI派发线程按固定节拍限流输出
    /// 4. 同一个点位在一个周期内变化多次，只保留最新一次
    /// 5. 断线后自动进入持续重连，而不是只尝试一次
    /// </summary>
    public class MqttService : IMqttService, IDisposable
    {
        public sealed class UiMqttMessage
        {
            public string Topic { get; init; } = string.Empty;
            public string Payload { get; init; } = string.Empty;
            public DateTime ReceivedAtUtc { get; init; }
        }

        private readonly ILogger _logger;
        private readonly IMqttClient _mqttClient;
        private readonly MqttClientOptions _options;

        // 替代 HashSet，避免多线程争用
        private readonly ConcurrentDictionary<string, byte> _subscribedTopics = new();

        public event Action<bool>? OnConnectionStatusChanged;

        /// <summary>
        /// 兼容旧代码：仍然支持逐条派发
        /// </summary>
        public event Func<string, string, Task>? OnMessageReceived;

        /// <summary>
        /// 推荐新事件：一次拿到一批变化项
        /// UI层应优先订阅这个事件
        /// </summary>
        public event Func<IReadOnlyList<UiMqttMessage>, Task>? OnBatchMessageReceived;

        // 原始 MQTT 收件队列：只负责从网络线程快速卸货
        private readonly Channel<(string Topic, string Payload)> _incomingChannel;

        // 最新状态缓存：Key = 主题 + 点位身份；Value = 该点位最新消息
        private readonly ConcurrentDictionary<string, UiMqttMessage> _latestMessageCache = new();

        // 脏标记：表示这个点位自上次派发后又更新过
        private readonly ConcurrentDictionary<string, byte> _dirtyKeys = new();

        private readonly CancellationTokenSource _cts = new();

        private readonly Task _incomingProcessTask;
        private readonly Task _dispatchTask;

        // 防止并发重复连接
        private readonly SemaphoreSlim _connectGate = new(1, 1);

        // 防止并发开启多个重连循环
        private int _reconnectLoopStarted = 0;

        private bool _disposed;

        // 下面这几个参数你后面可以抽到配置里
        private readonly int _incomingCapacity = 20000;
        private readonly int _maxDispatchCountPerTick = 200;   // 每个节拍最多往 UI 派发 200 条
        private readonly TimeSpan _dispatchInterval = TimeSpan.FromMilliseconds(500);

        public MqttService(ILogger logger)
        {
            _logger = logger;

            _incomingChannel = Channel.CreateBounded<(string Topic, string Payload)>(new BoundedChannelOptions(_incomingCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

            var factory = new MqttClientFactory();
            _mqttClient = factory.CreateMqttClient();

            _options = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", 1883)
                .WithClientId($"ScadaUI_Viewer_{Guid.NewGuid():N}")
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .Build();

            // MQTT 收到消息时，只做极轻量操作：快速入队，绝不碰 UI
            _mqttClient.ApplicationMessageReceivedAsync += e =>
            {
                try
                {
                    string topic = e.ApplicationMessage.Topic ?? string.Empty;
                    string payload = e.ApplicationMessage.Payload.IsEmpty
                        ? string.Empty
                        : Encoding.UTF8.GetString(BuffersExtensions.ToArray(e.ApplicationMessage.Payload));

                    if (!_incomingChannel.Writer.TryWrite((topic, payload)))
                    {
                        _logger.Warning("UI 原始消息队列已满，已丢弃最旧消息以保最新数据");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "接收 MQTT 消息并写入后台队列时发生异常");
                }

                return Task.CompletedTask;
            };

            _mqttClient.ConnectedAsync += async e =>
            {
                RaiseConnectionStatusChanged(true);
                _logger.Information("✅ UI 端 MQTT 已连接，开始恢复订阅");

                foreach (var topic in _subscribedTopics.Keys)
                {
                    try
                    {
                        var subOptions = new MqttClientSubscribeOptionsBuilder()
                            .WithTopicFilter(f => f.WithTopic(topic))
                            .Build();

                        await _mqttClient.SubscribeAsync(subOptions);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "恢复订阅失败 -> Topic: {Topic}", topic);
                    }
                }
            };

            _mqttClient.DisconnectedAsync += e =>
            {
                if (_cts.IsCancellationRequested || _disposed)
                    return Task.CompletedTask;

                RaiseConnectionStatusChanged(false);
                _logger.Warning("UI 端 MQTT 已断开，原因: {Reason}，启动持续重连机制...", e.Reason);

                _ = EnsureReconnectLoopAsync();
                return Task.CompletedTask;
            };

            _incomingProcessTask = Task.Run(() => ProcessIncomingMessagesAsync(_cts.Token));
            _dispatchTask = Task.Run(() => DispatchLoopAsync(_cts.Token));
        }

        /// <summary>
        /// 后台处理线程：把原始 MQTT 消息合并进“最新状态缓存”
        /// </summary>
        private async Task ProcessIncomingMessagesAsync(CancellationToken token)
        {
            _logger.Information("📥 UI 后台消息合并线程已启动");

            try
            {
                await foreach (var msg in _incomingChannel.Reader.ReadAllAsync(token))
                {
                    MergeIncomingPayload(msg.Topic, msg.Payload);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "UI 后台消息合并线程发生异常");
            }
        }

        /// <summary>
        /// 按固定节拍往 UI 派发，避免消息频率直接冲击渲染层
        /// </summary>
        private async Task DispatchLoopAsync(CancellationToken token)
        {
            _logger.Information("🖥️ UI 定时派发线程已启动，派发间隔: {Interval}ms", _dispatchInterval.TotalMilliseconds);

            try
            {
                using var timer = new PeriodicTimer(_dispatchInterval);

                while (await timer.WaitForNextTickAsync(token))
                {
                    if (_dirtyKeys.IsEmpty)
                        continue;

                    var batch = new List<UiMqttMessage>(_maxDispatchCountPerTick);

                    foreach (var key in _dirtyKeys.Keys)
                    {
                        if (batch.Count >= _maxDispatchCountPerTick)
                            break;

                        if (_dirtyKeys.TryRemove(key, out _)
                            && _latestMessageCache.TryGetValue(key, out var latest))
                        {
                            batch.Add(latest);
                        }
                    }

                    if (batch.Count == 0)
                        continue;

                    try
                    {
                        if (OnBatchMessageReceived != null)
                        {
                            await InvokeBatchMessageHandlersAsync(batch);
                        }
                        else if (OnMessageReceived != null)
                        {
                            await InvokeSingleMessageHandlersAsync(batch);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "派发 UI 批次消息时发生异常");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "UI 定时派发线程发生异常");
            }
        }

        /// <summary>
        /// 合并消息到最新缓存
        /// </summary>
        private void MergeIncomingPayload(string topic, string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return;

            try
            {
                var trimmed = payload.TrimStart();

                if (trimmed.StartsWith("["))
                {
                    using var doc = JsonDocument.Parse(payload);

                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        return;

                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        UpsertMessage(topic, element);
                    }
                }
                else if (trimmed.StartsWith("{"))
                {
                    using var doc = JsonDocument.Parse(payload);

                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return;

                    UpsertMessage(topic, doc.RootElement);
                }
                else
                {
                    var cacheKey = topic;

                    _latestMessageCache[cacheKey] = new UiMqttMessage
                    {
                        Topic = topic,
                        Payload = payload,
                        ReceivedAtUtc = DateTime.UtcNow
                    };

                    _dirtyKeys[cacheKey] = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "合并 MQTT 消息到最新缓存时发生异常 -> Topic: {Topic}", topic);
            }
        }

        private void UpsertMessage(string topic, JsonElement element)
        {
            var cacheKey = BuildCacheKey(topic, element);

            _latestMessageCache[cacheKey] = new UiMqttMessage
            {
                Topic = topic,
                Payload = element.GetRawText(),
                ReceivedAtUtc = DateTime.UtcNow
            };

            _dirtyKeys[cacheKey] = 0;
        }

        /// <summary>
        /// 根据强类型数据契约，精准提取“点位身份Key”
        /// 目标：性能极速化，彻底抛弃脆弱的关键字猜测
        /// </summary>
        private static string BuildCacheKey(string topic, JsonElement element)
        {
            string deviceId = string.Empty;
            string pointId = string.Empty;

            // 1. 精准提取 DeviceId (设备状态和点位数据都有这个字段)
            if (element.TryGetProperty("DeviceId", out var deviceIdProp))
            {
                deviceId = deviceIdProp.ValueKind == JsonValueKind.String
                    ? deviceIdProp.GetString() ?? string.Empty
                    : deviceIdProp.ToString();
            }

            // 2. 精准提取 PointId (只有点位数据有这个字段)
            if (element.TryGetProperty("PointId", out var pointIdProp))
            {
                pointId = pointIdProp.ValueKind == JsonValueKind.String
                    ? pointIdProp.GetString() ?? string.Empty
                    : pointIdProp.ToString();
            }

            // 🟢 场景 A：这是点位数据 (包含设备ID和点位ID)
            if (!string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(pointId))
            {
                return $"{topic}|{deviceId}|{pointId}";
            }

            // 🟢 场景 B：这是设备状态数据 (只包含设备ID)
            if (!string.IsNullOrEmpty(deviceId))
            {
                return $"{topic}|{deviceId}";
            }

            // 🟢 场景 C：未知格式的兜底方案 (直接用 Topic 覆盖)
            return topic;
        }



        public async Task ConnectAsync()
        {
            if (_disposed || _cts.IsCancellationRequested || _mqttClient.IsConnected)
                return;

            await _connectGate.WaitAsync();

            try
            {
                if (_disposed || _cts.IsCancellationRequested || _mqttClient.IsConnected)
                    return;

                await _mqttClient.ConnectAsync(_options);
                _logger.Information("UI 端 MQTT 连接成功");
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "UI 端 MQTT 连接失败");
            }
            finally
            {
                _connectGate.Release();
            }

            if (!_mqttClient.IsConnected && !_cts.IsCancellationRequested && !_disposed)
            {
                _ = EnsureReconnectLoopAsync();
            }
        }

        private async Task EnsureReconnectLoopAsync()
        {
            if (_disposed || _cts.IsCancellationRequested)
                return;

            if (Interlocked.Exchange(ref _reconnectLoopStarted, 1) == 1)
                return;

            try
            {
                int delaySeconds = 5;

                while (!_cts.IsCancellationRequested && !_disposed && !_mqttClient.IsConnected)
                {
                    try
                    {
                        _logger.Information("UI 端 MQTT 准备发起重连...");
                        await ConnectAsync();

                        if (_mqttClient.IsConnected)
                        {
                            _logger.Information("UI 端 MQTT 已成功重连");
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "UI 端 MQTT 重连失败，{Delay}s 后继续尝试", delaySeconds);
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    // 指数退避，最大 30 秒
                    delaySeconds = Math.Min(delaySeconds * 2, 30);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectLoopStarted, 0);
            }
        }

        public async Task<(bool IsSuccess, string ErrorMessage)> PublishAsync(string topic, string payload, bool retain = false)
        {
            try
            {
                if (!_mqttClient.IsConnected)
                    await ConnectAsync();

                if (!_mqttClient.IsConnected)
                    return (false, "MQTT 尚未连接，发送失败。");

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(retain)
                    .Build();

                await _mqttClient.PublishAsync(message);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "UI 端 MQTT 发布异常 -> Topic: {Topic}", topic);

                if (!_mqttClient.IsConnected && !_cts.IsCancellationRequested && !_disposed)
                {
                    _ = EnsureReconnectLoopAsync();
                }

                return (false, ex.Message);
            }
        }

        public async Task SubscribeAsync(string topic)
        {
            _subscribedTopics[topic] = 0;

            if (!_mqttClient.IsConnected)
            {
                await ConnectAsync();

                if (!_mqttClient.IsConnected)
                {
                    return;
                }
            }

            try
            {
                var subOptions = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(topic))
                    .Build();

                await _mqttClient.SubscribeAsync(subOptions);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "UI 立即订阅失败 -> Topic: {Topic}", topic);
            }
        }

        private void RaiseConnectionStatusChanged(bool isConnected)
        {
            var handlers = OnConnectionStatusChanged;
            if (handlers == null) return;

            foreach (Action<bool> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(isConnected);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "调用 OnConnectionStatusChanged 订阅者时发生异常");
                }
            }
        }

        private async Task InvokeBatchMessageHandlersAsync(IReadOnlyList<UiMqttMessage> batch)
        {
            var handlers = OnBatchMessageReceived;
            if (handlers == null) return;

            foreach (Func<IReadOnlyList<UiMqttMessage>, Task> handler in handlers.GetInvocationList())
            {
                try
                {
                    await handler(batch);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "调用 OnBatchMessageReceived 订阅者时发生异常");
                }
            }
        }

        private async Task InvokeSingleMessageHandlersAsync(IReadOnlyList<UiMqttMessage> batch)
        {
            var handlers = OnMessageReceived;
            if (handlers == null) return;

            foreach (var item in batch)
            {
                foreach (Func<string, string, Task> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        await handler(item.Topic, item.Payload);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "调用 OnMessageReceived 订阅者时发生异常");
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                _incomingChannel.Writer.TryComplete();
                _cts.Cancel();
            }
            catch
            {
                // 忽略释放期异常
            }
            finally
            {
                _mqttClient?.Dispose();
                _connectGate.Dispose();
                _cts.Dispose();
            }
        }
    }
}