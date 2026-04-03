using Collector.Contracts;
using Collector.Contracts.Enums;
using Collector.Contracts.Model;
using Collector.Contracts.Topics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contracts.Interface;
using HandyControl.Controls;
using Microsoft.Win32;
using MiniExcelLibs;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;

namespace Collector.UI.ViewModel
{
    public partial class Page1ViewModel : ObservableRecipient
    {
        private readonly ILogger _logger;
        private readonly string _configFilePath = "ScadaConfig.json";
        private readonly IMqttService _mqttService;

        // WPF 集合同步锁
        private static readonly object _collectionLock = new object();

        [ObservableProperty]
        private ObservableCollection<DeviceConfig> deviceConfigs = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDeviceSelected))]
        private DeviceConfig? selectedDevice;

        [ObservableProperty]
        private ICollectionView _filteredDeviceConfigs;

        [ObservableProperty]
        private ObservableCollection<string> availableWorkshops = new() { "全部车间", "配料车间", "封膜车间", "三车间" };

        [ObservableProperty]
        private string selectedWorkshopFilter = "全部车间";

        // 看门狗相关
        private DateTime _lastEdgeMessageTime = DateTime.MinValue;
        private bool _isEdgeConsideredOffline = true;
        private CancellationTokenSource _watchdogCts;

        // =========================
        // 实时 UI 削峰缓冲：新增字段
        // =========================
        private readonly ConcurrentQueue<PendingStatusUpdate> _pendingStatusQueue = new();
        private readonly ConcurrentQueue<PendingPointUpdate> _pendingPointQueue = new();

        private readonly DispatcherTimer _uiFlushTimer;

        // 运行时索引，不改变你原有数据结构
        private readonly Dictionary<string, DeviceConfig> _deviceIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PointConfig> _pointIndex = new(StringComparer.OrdinalIgnoreCase);

        private const int MaxStatusUpdatesPerTick = 500;
        private const int MaxPointUpdatesPerTick = 5000;

        private static readonly TimeSpan UiFlushInterval = TimeSpan.FromMilliseconds(200);
        private const int EdgeOfflineTimeoutSeconds = 15;

        public bool IsDeviceSelected => SelectedDevice is not null;

        private sealed class PendingStatusUpdate
        {
            public string DeviceId { get; init; } = string.Empty;
            public string WorkerStatus { get; init; } = string.Empty;
            public int StatusCode { get; init; }
        }

        private sealed class PendingPointUpdate
        {
            public string DeviceId { get; init; } = string.Empty;
            public string PointId { get; init; } = string.Empty;

            public object? RawValue { get; init; }
            public object? ProcessedValue { get; init; }
            public DateTime CollectTime { get; init; }
            public bool IsSuccess { get; init; }
            public string ErrorMessage { get; init; } = string.Empty;

            public string UniqueKey => $"{DeviceId}::{PointId}";
        }

        partial void OnSelectedWorkshopFilterChanged(string value)
        {
            FilteredDeviceConfigs.Refresh();
        }

        partial void OnDeviceConfigsChanged(ObservableCollection<DeviceConfig> value)
        {
            if (value != null)
            {
                FilteredDeviceConfigs = CollectionViewSource.GetDefaultView(value);
                FilteredDeviceConfigs.Filter = FilterDevice;
                RebuildRuntimeIndexes();
            }
        }

        public Page1ViewModel(ILogger logger, IMqttService mqttService)
        {
            _logger = logger;
            _mqttService = mqttService;

            BindingOperations.EnableCollectionSynchronization(DeviceConfigs, _collectionLock);
            OnDeviceConfigsChanged(DeviceConfigs);

            _uiFlushTimer = new DispatcherTimer
            {
                Interval = UiFlushInterval
            };
            _uiFlushTimer.Tick += UiFlushTimer_Tick;
            _uiFlushTimer.Start();
        }

        private bool FilterDevice(object obj)
        {
            if (SelectedWorkshopFilter == "全部车间") return true;

            if (obj is DeviceConfig device)
            {
                return device.Workshop == SelectedWorkshopFilter;
            }
            return false;
        }

        #region 设备操作 Commands

        [RelayCommand]
        private void AddDevice()
        {
            var newDevice = new DeviceConfig
            {
                DeviceId = $"PLC_{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}",
                DeviceName = $"设备_{DeviceConfigs.Count + 1}"
            };

            DeviceConfigs.Add(newDevice);
            SelectedDevice = newDevice;
            RebuildRuntimeIndexes();
        }

        [RelayCommand]
        private void DeleteDevice(DeviceConfig device)
        {
            if (device != null)
            {
                DeviceConfigs.Remove(device);
                if (SelectedDevice == device) SelectedDevice = null;
                RebuildRuntimeIndexes();
            }
        }

        #endregion

        #region 点位操作 Commands

        [RelayCommand]
        private void AddPoint()
        {
            if (SelectedDevice == null) return;

            SelectedDevice.Points.Add(new PointConfig
            {
                PointId = $"PT_{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}",
                PointName = $"点位_{SelectedDevice.Points.Count + 1}"
            });

            RebuildRuntimeIndexes();
        }

        [RelayCommand]
        private void DeletePoint(PointConfig point)
        {
            if (SelectedDevice != null && point != null)
            {
                SelectedDevice.Points.Remove(point);
                RebuildRuntimeIndexes();
            }
        }

        #endregion

        #region 全局操作 Commands

        [RelayCommand]
        private async Task StartCollectAsync()
        {
            if (DeviceConfigs.Count == 0)
            {
                Growl.Warning("请先配置设备和点位！");
                return;
            }

            _logger.Information("准备下发配置到后台服务...");
            Growl.Info("正在连接 MQTT Broker 下发配置...");

            var options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter());
            string configJson = JsonSerializer.Serialize(DeviceConfigs, options);

            var (isSuccess, errorMessage) = await _mqttService.PublishAsync(
                topic: CollectorTopics.ConfigUpdate,
                payload: configJson,
                retain: true);

            if (isSuccess)
            {
                _logger.Information("配置下发成功，通知后台重载引擎。");
                Growl.Success("采集任务已成功下发至后台引擎！");
            }
            else
            {
                Growl.Error($"下发配置失败，请检查 MQTT 服务是否开启！\n{errorMessage}");
            }
        }

        [RelayCommand]
        private async Task StopCollectAsync()
        {
            Growl.Info("正在发送停止指令...");
            _logger.Information("准备下发停止引擎指令...");

            var (isSuccess, errorMessage) = await _mqttService.PublishAsync(
                topic: CollectorTopics.EngineControl,
                payload: "stop",
                retain: false);

            if (isSuccess)
            {
                _logger.Information("停止指令下发成功。");
                Growl.Success("已通知后台引擎停止所有采集任务！");
            }
            else
            {
                Growl.Error($"停止指令下发失败，请检查网络！\n{errorMessage}");
            }
        }

        #endregion

        #region 实时数据监听 Commands 与 逻辑

        [RelayCommand]
        private async Task PageLoadedAsync()
        {
            try
            {
                await LoadConfigAsync();

                Growl.Info("正在连接事件总线，准备接收实时数据...");

                _mqttService.OnMessageReceived -= HandleEdgeMessage;

                if (_mqttService is Collector.UI.Service.MqttService uiMqttService)
                {
                    uiMqttService.OnBatchMessageReceived -= HandleEdgeBatchMessageAsync;
                    uiMqttService.OnBatchMessageReceived += HandleEdgeBatchMessageAsync;
                }
                else
                {
                    _mqttService.OnMessageReceived += HandleEdgeMessage;
                }

                await _mqttService.SubscribeAsync(CollectorTopics.GetDeviceStatusTopic("+"));
                await _mqttService.SubscribeAsync(CollectorTopics.GetDeviceStandDataTopic("+"));

                _mqttService.OnConnectionStatusChanged -= HandleMqttConnectionChanged;
                _mqttService.OnConnectionStatusChanged += HandleMqttConnectionChanged;

                _logger.Information("UI 端已成功启动 MQTT 实时数据监听！");
                Growl.Success("实时观察窗已连接！");

                _watchdogCts?.Cancel();
                _watchdogCts = new CancellationTokenSource();
                _ = WatchdogLoopAsync(_watchdogCts.Token);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "启动 UI 监听失败");
                Growl.Error("监听启动失败，请检查 MQTT 服务状态！");
            }
        }

        private Task HandleEdgeBatchMessageAsync(IReadOnlyList<Collector.UI.Service.MqttService.UiMqttMessage> batch)
        {
            if (batch == null || batch.Count == 0)
                return Task.CompletedTask;

            TouchEdgeHeartbeat();

            foreach (var item in batch)
            {
                TryEnqueueIncomingMessage(item.Topic, item.Payload);
            }

            return Task.CompletedTask;
        }

        private async Task HandleEdgeMessage(string topic, string payload)
        {
            TouchEdgeHeartbeat();
            TryEnqueueIncomingMessage(topic, payload);
            await Task.CompletedTask;
        }

        private void TouchEdgeHeartbeat()
        {
            _lastEdgeMessageTime = DateTime.Now;

            if (_isEdgeConsideredOffline)
            {
                _isEdgeConsideredOffline = false;
                Growl.Success("已收到边缘网关心跳数据，通信链路正常！");
            }
        }

        private void TryEnqueueIncomingMessage(string topic, string payload)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"\n[UI 收到情报] 主题: {topic}");

                if (topic.Contains("/status/"))
                {
                    using var doc = JsonDocument.Parse(payload);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("DeviceId", out var idElement))
                        return;

                    string incomingDeviceId = idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString() ?? ""
                        : idElement.ToString();

                    if (string.IsNullOrWhiteSpace(incomingDeviceId))
                        return;

                    string incomingStatus = "未知状态";
                    if (root.TryGetProperty("Status", out var statusElement))
                    {
                        incomingStatus = statusElement.ValueKind == JsonValueKind.String
                            ? statusElement.GetString() ?? ""
                            : statusElement.ToString();
                    }

                    int incomingStatusCode = 0;
                    if (root.TryGetProperty("StatusCode", out var codeElement))
                    {
                        if (codeElement.ValueKind == JsonValueKind.Number)
                        {
                            incomingStatusCode = codeElement.GetInt32();
                        }
                        else if (codeElement.ValueKind == JsonValueKind.String)
                        {
                            int.TryParse(codeElement.GetString(), out incomingStatusCode);
                        }
                    }

                    _pendingStatusQueue.Enqueue(new PendingStatusUpdate
                    {
                        DeviceId = incomingDeviceId,
                        WorkerStatus = incomingStatus,
                        StatusCode = incomingStatusCode
                    });
                }
                else if (topic.Contains("/data/") && topic.EndsWith("/standard"))
                {
                    var stdMsg = JsonSerializer.Deserialize<StandardPointData>(payload);
                    if (stdMsg == null || string.IsNullOrWhiteSpace(stdMsg.DeviceId) || string.IsNullOrWhiteSpace(stdMsg.PointId))
                        return;

                    _pendingPointQueue.Enqueue(new PendingPointUpdate
                    {
                        DeviceId = stdMsg.DeviceId,
                        PointId = stdMsg.PointId,
                        RawValue = stdMsg.RawValue,
                        ProcessedValue = stdMsg.ProcessedValue,
                        CollectTime = stdMsg.CollectTime,
                        IsSuccess = stdMsg.IsSuccess,
                        ErrorMessage = stdMsg.ErrorMessage ?? string.Empty
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI 解析报错] {ex.Message}");
            }
        }

        private void UiFlushTimer_Tick(object? sender, EventArgs e)
        {
            ApplyPendingStatusUpdates();
            ApplyPendingPointUpdates();
        }

        private void ApplyPendingStatusUpdates()
        {
            if (_pendingStatusQueue.IsEmpty)
                return;

            var latestByDevice = new Dictionary<string, PendingStatusUpdate>(StringComparer.OrdinalIgnoreCase);

            int count = 0;
            while (count < MaxStatusUpdatesPerTick && _pendingStatusQueue.TryDequeue(out var update))
            {
                latestByDevice[update.DeviceId] = update;
                count++;
            }

            foreach (var item in latestByDevice.Values)
            {
                if (_deviceIndex.TryGetValue(item.DeviceId, out var device))
                {
                    device.WorkerStatus = item.WorkerStatus;
                    device.StatusCode = item.StatusCode;
                }
            }
        }

        private void ApplyPendingPointUpdates()
        {
            if (_pendingPointQueue.IsEmpty)
                return;

            var latestByPoint = new Dictionary<string, PendingPointUpdate>(StringComparer.OrdinalIgnoreCase);

            int count = 0;
            while (count < MaxPointUpdatesPerTick && _pendingPointQueue.TryDequeue(out var update))
            {
                latestByPoint[update.UniqueKey] = update;
                count++;
            }

            foreach (var item in latestByPoint.Values)
            {
                if (_pointIndex.TryGetValue(item.UniqueKey, out var point))
                {
                    point.RawValue = item.RawValue;
                    point.ProcessedValue = item.ProcessedValue;
                    point.LastUpdateTime = item.CollectTime;
                    point.IsSuccess = item.IsSuccess;
                    point.ErrorMessage = item.ErrorMessage;
                }
            }
        }

        private async Task WatchdogLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(3000, token);

                if (_isEdgeConsideredOffline || _lastEdgeMessageTime == DateTime.MinValue)
                    continue;

                if ((DateTime.Now - _lastEdgeMessageTime).TotalSeconds > EdgeOfflineTimeoutSeconds)
                {
                    _isEdgeConsideredOffline = true;

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        foreach (var device in DeviceConfigs)
                        {
                            device.StatusCode = 0;
                            device.WorkerStatus = $"Edge端失联 (数据心跳超时 {EdgeOfflineTimeoutSeconds}s)";
                        }
                    });

                    Growl.Fatal($"🚨 边缘网关已超过 {EdgeOfflineTimeoutSeconds} 秒未上报任何数据，判定为失联！");
                    _logger.Warning("看门狗报警：边缘网关通信超时，超过 {Timeout}s 未收到 MQTT 报文。", EdgeOfflineTimeoutSeconds);
                }
            }
        }

        private void HandleMqttConnectionChanged(bool isConnected)
        {
            if (!isConnected)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var device in DeviceConfigs)
                    {
                        device.StatusCode = 0;
                        device.WorkerStatus = "UI端已断线(数据停滞)";
                    }
                });

                Growl.Warning("UI端与总线失去连接，画面数据已停滞！");
            }
            else
            {
                Growl.Info("UI端总线已重连，等待数据刷新...");
            }
        }

        private void RebuildRuntimeIndexes()
        {
            _deviceIndex.Clear();
            _pointIndex.Clear();

            foreach (var device in DeviceConfigs)
            {
                if (!string.IsNullOrWhiteSpace(device.DeviceId))
                {
                    _deviceIndex[device.DeviceId] = device;
                }

                foreach (var point in device.Points)
                {
                    if (string.IsNullOrWhiteSpace(device.DeviceId) || string.IsNullOrWhiteSpace(point.PointId))
                        continue;

                    _pointIndex[$"{device.DeviceId}::{point.PointId}"] = point;
                }
            }
        }

        #endregion

        #region 本地 JSON 配置存取 (持久化)

        [RelayCommand]
        private async Task SaveConfigAsync()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                options.Converters.Add(new JsonStringEnumConverter());

                string jsonString = JsonSerializer.Serialize(DeviceConfigs, options);
                await File.WriteAllTextAsync(_configFilePath, jsonString);

                Growl.Success("SCADA 配置已成功保存至本地！");
                _logger.Information("保存了 {Count} 个设备配置到 {Path}", DeviceConfigs.Count, _configFilePath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "保存配置失败");
                Growl.Error("保存失败，请检查日志或文件权限！");
            }
        }

        [RelayCommand]
        private async Task LoadConfigAsync()
        {
            try
            {
                if (!File.Exists(_configFilePath))
                {
                    Growl.Info("未找到本地配置文件，将使用空配置启动。");
                    return;
                }

                Growl.Info("正在读取本地配置...");
                string jsonString = await File.ReadAllTextAsync(_configFilePath);

                var options = new JsonSerializerOptions();
                options.Converters.Add(new JsonStringEnumConverter());

                var loadedConfigs = JsonSerializer.Deserialize<ObservableCollection<DeviceConfig>>(jsonString, options);

                if (loadedConfigs != null)
                {
                    foreach (var newDevice in loadedConfigs)
                    {
                        var oldDevice = DeviceConfigs.FirstOrDefault(d => d.DeviceId == newDevice.DeviceId);
                        if (oldDevice != null)
                        {
                            newDevice.StatusCode = oldDevice.StatusCode;
                            newDevice.WorkerStatus = oldDevice.WorkerStatus;

                            foreach (var newPoint in newDevice.Points)
                            {
                                var oldPoint = oldDevice.Points.FirstOrDefault(p => p.PointId == newPoint.PointId);
                                if (oldPoint != null)
                                {
                                    newPoint.CurrentValue = oldPoint.CurrentValue;
                                    newPoint.IsSuccess = oldPoint.IsSuccess;
                                    newPoint.ErrorMessage = oldPoint.ErrorMessage;
                                    newPoint.LastUpdateTime = oldPoint.LastUpdateTime;
                                }
                            }
                        }
                    }

                    DeviceConfigs = loadedConfigs;

                    BindingOperations.EnableCollectionSynchronization(DeviceConfigs, _collectionLock);

                    SelectedDevice = null;

                    Growl.Success($"成功读取 {DeviceConfigs.Count} 个本地设备配置！");
                    _logger.Information("手动加载配置成功，共 {Count} 个设备", DeviceConfigs.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "读取或解析本地配置文件时发生异常");
                Growl.Error("读取失败，可能是 JSON 格式已损坏，请查看日志！");
            }
        }

        #endregion

        #region Excel 导入与模板下载

        [RelayCommand]
        private void DownloadTemplate()
        {
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "Excel 文件|*.xlsx",
                FileName = "SCADA全量配置导入模板.xlsx",
                Title = "保存导入模板"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    var template = new[]
                    {
                        new { 所属车间="配料车间", 设备名称="1号产线_西门子", 协议类型="S71200", IP地址="127.0.0.1", 端口=102, 站号=1, 端点路由="", 用户名="", 密码="", 扫描周期=500, 点位名称="实际温度1", 寄存器地址="M100", 数据类型="Short", 长度=0, 比例=0.1, 偏移=-50.0, 表达式="x * 1.5 + 10", 死区=0.5, 点位周期=1000, 字节序="CDAB", 地址从0开始="True" },
                        new { 所属车间="配料车间", 设备名称="1号产线_西门子", 协议类型="S71200", IP地址="127.0.0.1", 端口=102, 站号=1, 端点路由="", 用户名="", 密码="", 扫描周期=500, 点位名称="运行状态", 寄存器地址="M140", 数据类型="Bool", 长度=0, 比例=1.0, 偏移=0.0, 表达式="", 死区=0.0, 点位周期=1000, 字节序="CDAB", 地址从0开始="True" },
                        new { 所属车间="封膜车间", 设备名称="2号_Modbus电表", 协议类型="ModbusTCP", IP地址="127.0.0.1", 端口=502, 站号=1, 端点路由="", 用户名="", 密码="", 扫描周期=500, 点位名称="当前电压", 寄存器地址="100", 数据类型="Int", 长度=0, 比例=1.0, 偏移=0.0, 表达式="", 死区=0.5, 点位周期=1000, 字节序="DCBA", 地址从0开始="True" },
                        new { 所属车间="配料车间", 设备名称="3号_OPC服务器", 协议类型="OpcUA", IP地址="127.0.0.1", 端口=53530, 站号=1, 端点路由="/OPCUA/SimulationServer", 用户名="", 密码="", 扫描周期=1000, 点位名称="随机温度", 寄存器地址="ns=3;i=1002", 数据类型="Float", 长度=0, 比例=1.0, 偏移=0.0, 表达式="", 死区=2.0, 点位周期=1000, 字节序="", 地址从0开始="" }
                    };

                    MiniExcel.SaveAs(saveFileDialog.FileName, template);
                    Growl.Success("模板下载成功！请严格按照模板的列名填写。");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "生成Excel模板失败");
                    Growl.Error("模板生成失败，文件可能被别的程序占用了！");
                }
            }
        }

        [RelayCommand]
        private void ImportExcel()
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Excel 文件|*.xlsx",
                Title = "选择配置好设备与点位的 Excel 文件"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                var dialogResult = System.Windows.MessageBox.Show(
                    "是否清空当前所有的设备和点位配置？\n\n" +
                    "【是】覆盖模式：清空现有列表，完全以 Excel 数据为准。\n" +
                    "【否】追加模式：保留现有列表，将 Excel 数据追加到末尾。",
                    "选择导入模式",
                    System.Windows.MessageBoxButton.YesNoCancel,
                    System.Windows.MessageBoxImage.Question);

                if (dialogResult == System.Windows.MessageBoxResult.Cancel)
                {
                    return;
                }

                try
                {
                    var rows = MiniExcel.Query(openFileDialog.FileName, useHeaderRow: true).Cast<IDictionary<string, object>>().ToList();

                    var tempDeviceDict = new Dictionary<string, DeviceConfig>();
                    int addedPointsCount = 0;

                    foreach (var row in rows)
                    {
                        if (!row.ContainsKey("设备名称") || string.IsNullOrWhiteSpace(row["设备名称"]?.ToString()) ||
                            !row.ContainsKey("点位名称") || string.IsNullOrWhiteSpace(row["点位名称"]?.ToString()))
                        {
                            continue;
                        }

                        string deviceName = row["设备名称"].ToString().Trim();
                        string workshopName = row.ContainsKey("所属车间") ? row["所属车间"]?.ToString()?.Trim() ?? "默认车间" : "默认车间";

                        string aggregateKey = $"[{workshopName}]_{deviceName}";

                        if (!tempDeviceDict.ContainsKey(aggregateKey))
                        {
                            ProtocolTypeEnum protocol = ProtocolTypeEnum.ModbusTCP;
                            if (row.ContainsKey("协议类型")) Enum.TryParse(row["协议类型"]?.ToString(), true, out protocol);

                            DataFormatEnum dataFormat = DataFormatEnum.CDAB;
                            if (row.ContainsKey("字节序")) Enum.TryParse(row["字节序"]?.ToString(), true, out dataFormat);

                            bool isStartWithZero = true;
                            if (row.ContainsKey("地址从0开始") && row["地址从0开始"] != null) bool.TryParse(row["地址从0开始"].ToString(), out isStartWithZero);

                            byte station = 1;
                            if (row.ContainsKey("站号") && row["站号"] != null) byte.TryParse(row["站号"].ToString(), out station);

                            tempDeviceDict[aggregateKey] = new DeviceConfig
                            {
                                DeviceId = $"PLC_{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}",
                                DeviceName = deviceName,
                                Workshop = workshopName,
                                ProtocolType = protocol,
                                IpAddress = row.ContainsKey("IP地址") ? row["IP地址"]?.ToString() ?? "127.0.0.1" : "127.0.0.1",
                                Port = row.ContainsKey("端口") ? Convert.ToInt32(row["端口"] ?? 502) : 502,
                                ScanIntervalMs = row.ContainsKey("扫描周期") ? Convert.ToInt32(row["扫描周期"] ?? 1000) : 1000,
                                DataFormat = dataFormat,
                                IsAddressStartWithZero = isStartWithZero,
                                Station = station,
                                OpcEndpointPath = row.ContainsKey("端点路由") ? row["端点路由"]?.ToString()?.Trim() ?? "" : "",
                                OpcUsername = row.ContainsKey("用户名") ? row["用户名"]?.ToString()?.Trim() ?? "" : "",
                                OpcPassword = row.ContainsKey("密码") ? row["密码"]?.ToString()?.Trim() ?? "" : "",
                            };
                        }

                        DataTypeEnum dataType = DataTypeEnum.Int;
                        if (row.ContainsKey("数据类型")) Enum.TryParse(row["数据类型"]?.ToString(), true, out dataType);

                        ushort length = 0;
                        if (row.ContainsKey("长度") && row["长度"] != null) ushort.TryParse(row["长度"].ToString(), out length);

                        double multiplier = 1.0;
                        if (row.ContainsKey("比例") && row["比例"] != null) double.TryParse(row["比例"].ToString(), out multiplier);

                        double offset = 0.0;
                        if (row.ContainsKey("偏移") && row["偏移"] != null) double.TryParse(row["偏移"].ToString(), out offset);

                        string expression = row.ContainsKey("表达式") ? row["表达式"]?.ToString()?.Trim() ?? "" : "";

                        double deadband = 0.0;
                        if (row.ContainsKey("死区") && row["死区"] != null) double.TryParse(row["死区"].ToString(), out deadband);

                        int pointInterval = 1000;
                        if (row.ContainsKey("点位周期") && row["点位周期"] != null) int.TryParse(row["点位周期"].ToString(), out pointInterval);

                        var newPoint = new PointConfig
                        {
                            PointId = $"PT_{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}",
                            PointName = row["点位名称"].ToString().Trim(),
                            Address = row.ContainsKey("寄存器地址") ? row["寄存器地址"]?.ToString()?.Trim() ?? "" : "",
                            DataType = dataType,
                            Length = length,
                            Multiplier = multiplier,
                            Offset = offset,
                            Expression = expression,
                            Deadband = deadband,
                            ScanIntervalMs = pointInterval
                        };

                        tempDeviceDict[aggregateKey].Points.Add(newPoint);
                        addedPointsCount++;
                    }

                    if (dialogResult == System.Windows.MessageBoxResult.Yes)
                    {
                        DeviceConfigs.Clear();
                        SelectedDevice = null;
                        _logger.Information("用户选择了覆盖导入，已清空当前所有配置。");
                    }

                    foreach (var device in tempDeviceDict.Values)
                    {
                        DeviceConfigs.Add(device);
                    }

                    RebuildRuntimeIndexes();

                    Growl.Success($"导入成功！共解析出 {tempDeviceDict.Count} 台设备，{addedPointsCount} 个点位！");
                    _logger.Information("Excel 导入成功：{DeviceCount} 台设备, {PointCount} 个点位", tempDeviceDict.Count, addedPointsCount);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Excel 导入失败");
                    Growl.Error("导入失败！请检查文件格式、枚举拼写，或文件是否正被 Excel 打开。");
                }
            }
        }

        #endregion
    }
}