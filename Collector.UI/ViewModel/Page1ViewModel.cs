using Collector.Contracts;
using Collector.Contracts.Model;
using Collector.Contracts.Topics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contracts.Interface;
using HandyControl.Controls;
using MQTTnet;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data; // 🟢 必须引入这个，才能使用跨线程集合同步机制
using System.ComponentModel; // 🟢 必须引入


namespace Collector.UI.ViewModel
{
    public partial class Page1ViewModel : ObservableRecipient
    {
        private readonly ILogger _logger;
        private readonly string _configFilePath = "ScadaConfig.json";

        private readonly IMqttService _mqttService;

        // 🟢 1. 声明一个静态锁对象，专供 WPF 底层排队调度使用
        private static readonly object _collectionLock = new object();

        [ObservableProperty]
        private ObservableCollection<DeviceConfig> deviceConfigs = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDeviceSelected))]
        private DeviceConfig? selectedDevice;

        // 🟢 1. 定义一个用于 UI 绑定的“集合视图”

        [ObservableProperty]
        private ICollectionView _filteredDeviceConfigs;

        // 🟢 2. 定义车间筛选的下拉框数据源 (你可以随时增删车间)
        [ObservableProperty]
        private ObservableCollection<string> availableWorkshops = new() { "全部车间", "配料车间", "封膜车间", "三车间" };

        // 🟢 3. 定义当前选中的筛选条件
        [ObservableProperty]
        private string selectedWorkshopFilter = "全部车间";

        // 🟢 4. 当选中的筛选条件发生变化时，通知视图刷新！
        // (这是 CommunityToolkit 的伟大魔法，自动捕获 selectedWorkshopFilter 的改变)
        partial void OnSelectedWorkshopFilterChanged(string value)
        {
            FilteredDeviceConfigs.Refresh();
        }

        // 🟢 2. 核心拦截器：无论谁（包括 LoadConfig）替换了底层集合，立刻重新生成“有色眼镜”！
        partial void OnDeviceConfigsChanged(ObservableCollection<DeviceConfig> value)
        {
            if (value != null)
            {
                FilteredDeviceConfigs = CollectionViewSource.GetDefaultView(value);
                FilteredDeviceConfigs.Filter = FilterDevice; // 重新挂载过滤条件
            }
        }

        public bool IsDeviceSelected => SelectedDevice is not null;

        public Page1ViewModel(ILogger logger, IMqttService mqttService)
        {
            _logger = logger;
            _mqttService = mqttService;

            // 🟢 2. 开启 WPF 原生黑科技：允许后台线程直接修改集合，彻底告别假死！
            BindingOperations.EnableCollectionSynchronization(DeviceConfigs, _collectionLock);

            // 🟢 3. 初始化默认视图 (手动触发一次钩子)
            OnDeviceConfigsChanged(DeviceConfigs);
        }



        private bool FilterDevice(object obj)
        {
            if (SelectedWorkshopFilter == "全部车间") return true; // 选了“全部”就全放行

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

            // 🟢 必须生成一个唯一 ID！这里用时间戳+序号，或者干脆用 Guid
           
          

            var newDevice = new DeviceConfig {
                DeviceId = $"PLC_{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}", 
                DeviceName = $"设备_{DeviceConfigs.Count + 1}" };
            DeviceConfigs.Add(newDevice);
            SelectedDevice = newDevice;
        }

        [RelayCommand]
        private void DeleteDevice(DeviceConfig device)
        {
            if (device != null)
            {
                DeviceConfigs.Remove(device);
                if (SelectedDevice == device) SelectedDevice = null;
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
                // 🟢 同样必须生成点位 ID！
                PointId = $"PT_{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}",
                PointName = $"点位_{SelectedDevice.Points.Count + 1}"
            });
        }

        [RelayCommand]
        private void DeletePoint(PointConfig point)
        {
            if (SelectedDevice != null && point != null)
            {
                SelectedDevice.Points.Remove(point);
            }
        }

        #endregion

        #region 全局操作 Commands

        [RelayCommand]
        private async Task SaveConfigAsync()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                options.Converters.Add(new JsonStringEnumConverter());
                string jsonString = JsonSerializer.Serialize(DeviceConfigs, options);
                await File.WriteAllTextAsync(_configFilePath, jsonString);

                Growl.Success("SCADA 配置已成功保存！");
                _logger.Information("保存了 {Count} 个设备配置", DeviceConfigs.Count);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "保存配置失败");
                Growl.Error("保存失败，请检查日志");
            }
        }

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

            // 向控制频道发送简单的文本指令 "stop"
            var (isSuccess, errorMessage) = await _mqttService.PublishAsync(
                topic: CollectorTopics.EngineControl,
                payload: "stop",
                retain: false); // 指令不需要保留，只对当前在线的 Edge 有效

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

        [RelayCommand]
        private async Task LoadConfigAsync()
        {
            try
            {
                if (!File.Exists(_configFilePath))
                {
                    Growl.Warning("未找到本地配置文件！");
                    return;
                }

                Growl.Info("正在读取配置...");
                string jsonString = await File.ReadAllTextAsync(_configFilePath);

                var options = new JsonSerializerOptions();
                options.Converters.Add(new JsonStringEnumConverter());

                var loadedConfigs = JsonSerializer.Deserialize<ObservableCollection<DeviceConfig>>(jsonString, options);

                if (loadedConfigs != null)
                {
                    DeviceConfigs = loadedConfigs;
                    SelectedDevice = null;

                    Growl.Success($"成功读取 {DeviceConfigs.Count} 个设备配置！");
                    _logger.Information("手动加载配置成功，共 {Count} 个设备", DeviceConfigs.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "读取或解析配置文件时发生异常");
                Growl.Error("读取失败，可能是文件格式不匹配，请查看日志！");
            }
        }

        #region 实时数据监听 Commands 与 逻辑

        [RelayCommand]
        private async Task PageLoadedAsync()
        {
            try
            {

                // 🟢 1. 第一步：先静默加载本地配置文件！把花名册建好！
                // 直接复用你已经写好的 LoadConfigAsync 逻辑
                await LoadConfigAsync();


                Growl.Info("正在连接事件总线，准备接收实时数据...");

                _mqttService.OnMessageReceived -= HandleEdgeMessage;
                _mqttService.OnMessageReceived += HandleEdgeMessage;

                await _mqttService.SubscribeAsync(CollectorTopics.GetDeviceStatusTopic("+"));
                await _mqttService.SubscribeAsync(CollectorTopics.GetDeviceRawDataTopic("+"));

                _logger.Information("UI 端已成功启动 MQTT 实时数据监听！");
                Growl.Success("实时观察窗已连接！");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "启动 UI 监听失败");
                Growl.Error("监听启动失败，请检查 MQTT 服务状态！");
            }
        }

        // 核心解析逻辑：彻底解脱！不用切回主线程了！
        private async Task HandleEdgeMessage(string topic, string payload)
        {
            // 调试用：在 VS 的输出窗口实时打印
            System.Diagnostics.Debug.WriteLine($"\n[UI 收到情报] 主题: {topic}");

            try
            {
                // 🚀 3. 看这里！Dispatcher.Invoke 已经被删掉了，代码瞬间清爽！
                if (topic.Contains("/status/"))
                {
                    using var doc = JsonDocument.Parse(payload);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("DeviceId", out var idElement))
                    {
                        string incomingDeviceId = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() ?? "" : idElement.ToString();

                        string incomingStatus = "未知状态";
                        if (root.TryGetProperty("Status", out var statusElement))
                        {
                            incomingStatus = statusElement.ValueKind == JsonValueKind.String ? statusElement.GetString() ?? "" : statusElement.ToString();
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

                        var device = DeviceConfigs.FirstOrDefault(d => d.DeviceId == incomingDeviceId);
                        if (device != null)
                        {
                            device.WorkerStatus = incomingStatus;
                            device.StatusCode = incomingStatusCode;
                        }
                    }
                }
                else if (topic.Contains("/data/") && topic.EndsWith("/raw"))
                {
                    var rawMsg = JsonSerializer.Deserialize<RawMessage>(payload);
                    if (rawMsg == null) return;

                    var device = DeviceConfigs.FirstOrDefault(d => d.DeviceId == rawMsg.DeviceId);
                    if (device != null)
                    {
                        var point = device.Points.FirstOrDefault(p => p.PointId == rawMsg.PointId);
                        if (point != null)
                        {
                            point.CurrentValue = rawMsg.Value;
                            point.LastUpdateTime = rawMsg.CollectTime;
                            point.IsSuccess = rawMsg.IsSuccess;
                            point.ErrorMessage = rawMsg.ErrorMessage;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI 解析报错] {ex.Message}");
            }

            await Task.CompletedTask;
        }

        #endregion
    }
}