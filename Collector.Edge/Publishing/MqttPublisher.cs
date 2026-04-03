using Collector.Contracts;
using Collector.Contracts.Model;
using Collector.Contracts.Topics;
using Contracts.Interface;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Collector.Edge.Publishing
{
    public interface IMqttPublisher
    {
        Task PublishPointDataAsync(StandardPointData message);
        Task PublishDeviceStatusAsync(string deviceId, string status, int statuscode);
    }

    /// <summary>
    /// MQTT 业务发布者
    /// 核心目标：
    /// 1. Worker 只负责把数据推入内存管道，不阻塞采集线程
    /// 2. 背景线程按批次、按设备聚合发送
    /// 3. 超过容量时丢最旧数据，优先保证“最新状态”送出
    /// </summary>
    public class MqttPublisher : IMqttPublisher, IDisposable
    {
        private readonly ILogger<MqttPublisher> _logger;
        private readonly IMqttService _mqttService;
        private readonly IOptionsMonitor<SystemOptions> _sysOptions;

        private readonly Channel<StandardPointData> _dataChannel;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _consumerTask;

        private readonly int _batchSize;
        private readonly TimeSpan _flushInterval;

        public MqttPublisher(
            ILogger<MqttPublisher> logger,
            IMqttService mqttService,
            IOptionsMonitor<SystemOptions> sysOptions)
        {
            _logger = logger;
            _mqttService = mqttService;
            _sysOptions = sysOptions;

            _batchSize = _sysOptions.CurrentValue.PublisherChannelReadderCount;

     
            double flushSeconds = Convert.ToDouble(_sysOptions.CurrentValue.PublisherIntervalMilliSeconds);
            if (flushSeconds <= 0)
            {
                flushSeconds = 1;
            }
            _flushInterval = TimeSpan.FromMilliseconds(flushSeconds);

            int capacity = _sysOptions.CurrentValue.PublisherCapacity;
            if (capacity <= 0)
            {
                capacity = 50000;
            }

            _dataChannel = Channel.CreateBounded<StandardPointData>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

            _consumerTask = Task.Run(() => ConsumeLoopAsync(_cts.Token));
        }

        /// <summary>
        /// 采集线程只负责快速入队，不参与真正发送
        /// </summary>
        public async Task PublishPointDataAsync(StandardPointData message)
        {
            if (message == null) return;

            try
            {
                if (!_dataChannel.Writer.TryWrite(message))
                {
                    await _dataChannel.Writer.WriteAsync(message, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出时忽略
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据推入 MQTT 发布管道时发生异常");
            }
        }

        /// <summary>
        /// 后台消费者：按“批量 + 超时”双条件发车
        /// </summary>
        private async Task ConsumeLoopAsync(CancellationToken token)
        {
            _logger.LogInformation("🚛 MqttPublisher 批量发送线程已启动，批量大小: {BatchSize}，刷新周期: {FlushInterval}ms",
                _batchSize, _flushInterval.TotalMilliseconds);

            var buffer = new List<StandardPointData>(_batchSize);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 先等第一条，避免空转
                    var first = await _dataChannel.Reader.ReadAsync(token);
                    buffer.Add(first);

                    var flushDeadline = DateTime.UtcNow + _flushInterval;

                    // 在 flushDeadline 之前尽可能继续凑批次
                    while (buffer.Count < _batchSize && !token.IsCancellationRequested)
                    {
                        var remain = flushDeadline - DateTime.UtcNow;
                        if (remain <= TimeSpan.Zero)
                            break;

                        var waitReadTask = _dataChannel.Reader.WaitToReadAsync(token).AsTask();
                        var delayTask = Task.Delay(remain, token);

                        var completed = await Task.WhenAny(waitReadTask, delayTask);

                        if (completed == delayTask)
                            break;

                        if (await waitReadTask)
                        {
                            while (buffer.Count < _batchSize && _dataChannel.Reader.TryRead(out var item))
                            {
                                buffer.Add(item);
                            }
                        }
                    }

                    await PublishBatchAsync(buffer, token);
                    buffer.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MqttPublisher 后台发送线程发生严重异常");
            }
        }

        /// <summary>
        /// 按设备分组后发布，每个设备一个数组消息
        /// </summary>
        private async Task PublishBatchAsync(List<StandardPointData> batch, CancellationToken token)
        {
            if (batch == null || batch.Count == 0 || token.IsCancellationRequested)
                return;

            try
            {
                var grouped = batch.GroupBy(x => x.DeviceId);

                foreach (var group in grouped)
                {
                    if (token.IsCancellationRequested)
                        break;

                    var deviceId = group.Key;
                    var topic = CollectorTopics.GetDeviceStandDataTopic(deviceId);
                    var payload = JsonSerializer.Serialize(group.ToList());

                    var result = await _mqttService.PublishAsync(topic, payload, retain: true);

                    if (!result.IsSuccess)
                    {
                        _logger.LogWarning("批量发布失败 -> DeviceId: {DeviceId}, Error: {Error}", deviceId, result.ErrorMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发布批次数据时发生异常");
            }
        }

        /// <summary>
        /// 状态类消息低频且重要，直接发送
        /// </summary>
        public async Task PublishDeviceStatusAsync(string deviceId, string status, int statuscode)
        {
            try
            {
                var topic = CollectorTopics.GetDeviceStatusTopic(deviceId);

                var statusMsg = new DeviceStatusMessage
                {
                    DeviceId = deviceId,
                    Status = status,
                    StatusCode = statuscode
                };

                var payload = JsonSerializer.Serialize(statusMsg);
                var result = await _mqttService.PublishAsync(topic, payload, retain: true);

                if (!result.IsSuccess)
                {
                    _logger.LogWarning("设备状态发布失败 -> DeviceId: {DeviceId}, Error: {Error}", deviceId, result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发布设备状态时发生异常 -> DeviceId: {DeviceId}", deviceId);
            }
        }

        public void Dispose()
        {
            try
            {
                _dataChannel.Writer.TryComplete();
                _cts.Cancel();
            }
            catch
            {
                // 忽略释放期异常
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }
}