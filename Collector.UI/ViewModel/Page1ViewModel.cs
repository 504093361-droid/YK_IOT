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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel; // 🟢 必须引入
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data; // 🟢 必须引入这个，才能使用跨线程集合同步机制


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
        // 在类顶部的变量声明区域加入：
        private DateTime _lastEdgeMessageTime = DateTime.MinValue;
        private bool _isEdgeConsideredOffline = true; // 默认刚启动时认为是离线的
        private CancellationTokenSource _watchdogCts;


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
                await _mqttService.SubscribeAsync(CollectorTopics.GetDeviceStandDataTopic("+"));

                // 🟢 1. 订阅连接状态变化事件
                _mqttService.OnConnectionStatusChanged -= HandleMqttConnectionChanged;
                _mqttService.OnConnectionStatusChanged += HandleMqttConnectionChanged;

                _logger.Information("UI 端已成功启动 MQTT 实时数据监听！");
                Growl.Success("实时观察窗已连接！");

                // 🟢 启动看门狗！
                _watchdogCts?.Cancel(); // 防止重复加载产生多个狗
                _watchdogCts = new CancellationTokenSource();
                _ = WatchdogLoopAsync(_watchdogCts.Token); // 后台跑，不管它

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "启动 UI 监听失败");
                Growl.Error("监听启动失败，请检查 MQTT 服务状态！");
            }
        }
        // 🟢 工业级看门狗巡逻逻辑
        private async Task WatchdogLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // 每隔 3 秒醒来巡逻一次
                await Task.Delay(3000, token);

                // 如果还没连上过，或者本来就判定为离线了，就不叫唤
                if (_isEdgeConsideredOffline || _lastEdgeMessageTime == DateTime.MinValue)
                    continue;

                // 🟢 判定生死线：如果当前时间距离最后一次收到数据超过了 60秒！
                if ((DateTime.Now - _lastEdgeMessageTime).TotalSeconds > 60)
                {
                    _isEdgeConsideredOffline = true; // 标记为已死

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        foreach (var device in DeviceConfigs)
                        {
                            device.StatusCode = 0; // 全部涂灰
                            device.WorkerStatus = "Edge端失联 (数据心跳超时)";
                        }
                    });

                    Growl.Fatal("🚨 边缘网关已超过 15 秒未上报任何数据，判定为失联！");
                    _logger.Warning("看门狗报警：边缘网关通信超时，超过15秒未收到 MQTT 报文。");
                }
            }
        }


        // 🟢 2. 新增处理逻辑：UI 断线时，瞬间涂灰所有设备！
        private void HandleMqttConnectionChanged(bool isConnected)
        {
            // 如果断线了
            if (!isConnected)
            {
                // 将 WPF 绑定推回 UI 线程（防止多线程跨域修改 UI 属性报错）
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var device in DeviceConfigs)
                    {
                        device.StatusCode = 0; // 0 会触发你在 XAML 里写的默认灰色样式
                        device.WorkerStatus = "UI端已断线(数据停滞)";
                    }
                });

                Growl.Warning("UI端与总线失去连接，画面数据已停滞！");
            }
            else
            {
                // 如果连上了，不需要手动改状态，让 MQTT 发过来的 Retain 消息自然刷新它即可！
                Growl.Info("UI端总线已重连，等待数据刷新...");
            }
        }



        // 核心解析逻辑：彻底解脱！不用切回主线程了！
        private async Task HandleEdgeMessage(string topic, string payload)
        {


            // 调试用：在 VS 的输出窗口实时打印
            System.Diagnostics.Debug.WriteLine($"\n[UI 收到情报] 主题: {topic}");
            // 🟢 1. 核心大招：只要收到任何消息，立刻刷新最后通讯时间！（喂狗）
            _lastEdgeMessageTime = DateTime.Now;

            // 🟢 2. 如果之前是离线状态，现在诈尸了，给个提示
            if (_isEdgeConsideredOffline)
            {
                _isEdgeConsideredOffline = false;
                Growl.Success("已收到边缘网关心跳数据，通信链路正常！");
            }





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
                else if (topic.Contains("/data/") && topic.EndsWith("/standard"))
                {
                    var stdMsg = JsonSerializer.Deserialize<StandardPointData>(payload);
                    if (stdMsg == null) return;

                    var device = DeviceConfigs.FirstOrDefault(d => d.DeviceId == stdMsg.DeviceId);

                    if (device != null)
                    {
                        var point = device.Points.FirstOrDefault(p => p.PointId == stdMsg.PointId);
                        if (point != null)
                        {
                            // 🟢 UI 同时装载生肉和熟肉！
                            point.RawValue = stdMsg.RawValue;
                            point.ProcessedValue = stdMsg.ProcessedValue;
                            point.LastUpdateTime = stdMsg.CollectTime;
                            point.IsSuccess = stdMsg.IsSuccess;
                            point.ErrorMessage = stdMsg.ErrorMessage;
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

        #region 本地 JSON 配置存取 (持久化)

        // 配置文件路径 (确保你的类顶部有这个变量: private readonly string _configFilePath = "ScadaConfig.json";)

        [RelayCommand]
        private async Task SaveConfigAsync()
        {
            try
            {
                // 1. 设置 JSON 序列化选项：缩进排版好看，并且将枚举存为字符串(如 "ModbusTCP")
                var options = new JsonSerializerOptions { WriteIndented = true };
                options.Converters.Add(new JsonStringEnumConverter());

                // 2. 序列化当前的 DeviceConfigs 集合
                string jsonString = JsonSerializer.Serialize(DeviceConfigs, options);

                // 3. 异步写入本地文件
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

                // 1. 先反序列化成一个新的临时集合
                var loadedConfigs = JsonSerializer.Deserialize<ObservableCollection<DeviceConfig>>(jsonString, options);

                if (loadedConfigs != null)
                {
                    // 🟢 2. 核心大招：运行时状态继承！(防止热重载时 UI 红绿灯和数值变灰)
                    foreach (var newDevice in loadedConfigs)
                    {
                        var oldDevice = DeviceConfigs.FirstOrDefault(d => d.DeviceId == newDevice.DeviceId);
                        if (oldDevice != null)
                        {
                            // 继承设备的宏观红绿灯状态
                            newDevice.StatusCode = oldDevice.StatusCode;
                            newDevice.WorkerStatus = oldDevice.WorkerStatus;

                            // 继承底层点位的实时跳动数据
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

                    // 3. 完美覆盖，触发底层 OnDeviceConfigsChanged 钩子，重新生成过滤视图
                    DeviceConfigs = loadedConfigs;

                    // 🟢 极其关键的一把锁！因为换了新集合，必须重新给新集合上跨线程锁！
                    BindingOperations.EnableCollectionSynchronization(DeviceConfigs, _collectionLock);

                    // 清空当前选中项，避免越界或显示异常
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

   

// ... 在 Page1ViewModel 中 ...

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
                    // 🟢 构造工业级平铺模板，展示了如何把同设备的点位写在一起
                    var template = new[]
        {
    new { 所属车间="配料车间", 设备名称="1号产线_西门子", 协议类型="S71200", IP地址="127.0.0.1", 端口=102, 扫描周期=500, 点位名称="实际温度1", 寄存器地址="M100", 数据类型="Short", 长度=0, 比例=0.1, 偏移=-50.0, 表达式="x * 1.5 + 10", 死区=0.5, 点位周期=1000, 字节序="CDAB", 地址从0开始="True" },
    new { 所属车间="配料车间", 设备名称="1号产线_西门子", 协议类型="S71200", IP地址="127.0.0.1", 端口=102, 扫描周期=500, 点位名称="实际温度2", 寄存器地址="M110", 数据类型="Int", 长度=0, 比例=1.0, 偏移=0.0, 表达式="", 死区=0.0, 点位周期=1000, 字节序="CDAB", 地址从0开始="True" },
    new { 所属车间="配料车间", 设备名称="1号产线_西门子", 协议类型="S71200", IP地址="127.0.0.1", 端口=102, 扫描周期=500, 点位名称="实际温度3", 寄存器地址="M120", 数据类型="Float", 长度=0, 比例=1.0, 偏移=0.0, 表达式="", 死区=0.0, 点位周期=1000, 字节序="CDAB", 地址从0开始="True" },
    new { 所属车间="配料车间", 设备名称="1号产线_西门子", 协议类型="S71200", IP地址="127.0.0.1", 端口=102, 扫描周期=500, 点位名称="实际温度4", 寄存器地址="M130", 数据类型="Int", 长度=0, 比例=1.0, 偏移=0.0, 表达式="", 死区=0.0, 点位周期=1000, 字节序="CDAB", 地址从0开始="True" },
    new { 所属车间="配料车间", 设备名称="1号产线_西门子", 协议类型="S71200", IP地址="127.0.0.1", 端口=102, 扫描周期=500, 点位名称="运行状态", 寄存器地址="M140", 数据类型="Bool", 长度=0, 比例=1.0, 偏移=0.0, 表达式="", 死区=0.0, 点位周期=1000, 字节序="CDAB", 地址从0开始="True" },
    new { 所属车间="封膜车间", 设备名称="2号_Modbus电表", 协议类型="ModbusTCP", IP地址="127.0.0.1", 端口=502, 扫描周期=500, 点位名称="当前电压", 寄存器地址="100", 数据类型="Int", 长度=0, 比例=1.0, 偏移=0.0, 表达式="", 死区=0.5, 点位周期=1000, 字节序="CDAB", 地址从0开始="True" },
    new { 所属车间="封膜车间", 设备名称="2号_Modbus电表", 协议类型="ModbusTCP", IP地址="127.0.0.1", 端口=502, 扫描周期=500, 点位名称="产品条码", 寄存器地址="200", 数据类型="String", 长度=10, 比例=1.0, 偏移=0.0, 表达式="", 死区=0.5, 点位周期=1000, 字节序="CDAB", 地址从0开始="True" }
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

                // 🟢 1. 导入前，把选择权交给操作员！
                var dialogResult = System.Windows.MessageBox.Show(
                    "是否清空当前所有的设备和点位配置？\n\n" +
                    "【是】覆盖模式：清空现有列表，完全以 Excel 数据为准。\n" +
                    "【否】追加模式：保留现有列表，将 Excel 数据追加到末尾。",
                    "选择导入模式",
                    System.Windows.MessageBoxButton.YesNoCancel,
                    System.Windows.MessageBoxImage.Question);

                // 如果选了取消，直接终止导入
                if (dialogResult == System.Windows.MessageBoxResult.Cancel)
                {
                    return;
                }



                try
            {
                // 1. 读取 Excel 所有行（以字典形式，Key 为表头）
                var rows = MiniExcel.Query(openFileDialog.FileName, useHeaderRow: true).Cast<IDictionary<string, object>>().ToList();

                // 2. 准备一个临时字典，用来“按设备名称聚合”设备
                var tempDeviceDict = new Dictionary<string, DeviceConfig>();
                int addedPointsCount = 0;




                foreach (var row in rows)
                {
                    // 容错：如果连设备名称或点位名称都没有，直接跳过这一行
                    if (!row.ContainsKey("设备名称") || string.IsNullOrWhiteSpace(row["设备名称"]?.ToString()) ||
                        !row.ContainsKey("点位名称") || string.IsNullOrWhiteSpace(row["点位名称"]?.ToString()))
                    {
                        continue;
                    }

                    string deviceName = row["设备名称"].ToString().Trim();
                        string workshopName = row.ContainsKey("所属车间") ? row["所属车间"]?.ToString()?.Trim() ?? "默认车间" : "默认车间";

                        // 🟢 核心重构：使用“车间+设备”作为聚合的主键，防止不同车间同名设备发生交叉污染！
                        string aggregateKey = $"[{workshopName}]_{deviceName}";


                        // 🟢 核心聚合逻辑：如果字典里还没这个设备，就先造一个出来！
                        if (!tempDeviceDict.ContainsKey(aggregateKey))
                        {
                            ProtocolTypeEnum protocol = ProtocolTypeEnum.ModbusTCP;
                            if (row.ContainsKey("协议类型")) Enum.TryParse(row["协议类型"]?.ToString(), true, out protocol);

                            tempDeviceDict[aggregateKey] = new DeviceConfig
                            {
                                DeviceId = $"PLC_{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}",
                                DeviceName = deviceName, // 真实名称依然保持纯净
                                Workshop = workshopName, // 车间名
                                ProtocolType = protocol,
                                IpAddress = row.ContainsKey("IP地址") ? row["IP地址"]?.ToString() ?? "127.0.0.1" : "127.0.0.1",
                                Port = row.ContainsKey("端口") ? Convert.ToInt32(row["端口"] ?? 502) : 502,
                                ScanIntervalMs = row.ContainsKey("扫描周期") ? Convert.ToInt32(row["扫描周期"] ?? 1000) : 1000,
                            };
                        }

                        // 🟢 给这个设备塞入当前行的点位
                        DataTypeEnum dataType = DataTypeEnum.Int;
                    if (row.ContainsKey("数据类型")) Enum.TryParse(row["数据类型"]?.ToString(), true, out dataType);

                    ushort length = 0;
                    if (row.ContainsKey("长度") && row["长度"] != null) ushort.TryParse(row["长度"].ToString(), out length);

                        // 🟢 新增：解析 比例 (k) 和 偏移 (b)，为了防呆，给个默认值 1.0 和 0.0
                        double multiplier = 1.0;
                        if (row.ContainsKey("比例") && row["比例"] != null) double.TryParse(row["比例"].ToString(), out multiplier);

                        double offset = 0.0;
                        if (row.ContainsKey("偏移") && row["偏移"] != null) double.TryParse(row["偏移"].ToString(), out offset);
                        // 🟢 新增：解析表达式
                        string expression = row.ContainsKey("表达式") ? row["表达式"]?.ToString()?.Trim() ?? "" : "";

                        // 🟢 新增：死区解析
                        double deadband = 0.0;
                        if (row.ContainsKey("死区") && row["死区"] != null) double.TryParse(row["死区"].ToString(), out deadband);

                        // 🟢 解析点位周期 (防呆默认给 1000)
                        int pointInterval = 1000;
                        if (row.ContainsKey("点位周期") && row["点位周期"] != null) int.TryParse(row["点位周期"].ToString(), out pointInterval);


                        var newPoint = new PointConfig
                    {
                        PointId = $"PT_{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}",
                        PointName = row["点位名称"].ToString().Trim(),
                        Address = row.ContainsKey("寄存器地址") ? row["寄存器地址"]?.ToString()?.Trim() ?? "" : "",
                        DataType = dataType,
                        Length = length,
                            Multiplier = multiplier, // 🟢 补上这一行！
                            Offset = offset ,         // 🟢 补上这一行！
                            Expression = expression,  // 🟢 赋值表达式
                            Deadband = deadband, // 🟢 赋值死区
                            ScanIntervalMs = pointInterval // 🟢 赋值点位周期


                        };

                        tempDeviceDict[aggregateKey].Points.Add(newPoint);
                        addedPointsCount++;
                    }

                    // 🟢 2. 根据操作员的选择，决定要不要清空数据源
                    if (dialogResult == System.Windows.MessageBoxResult.Yes)
                    {
                        DeviceConfigs.Clear();
                        SelectedDevice = null; // 必须清空选中项，否则 UI 可能会因为找不到引用而报错
                        _logger.Information("用户选择了覆盖导入，已清空当前所有配置。");
                    }


                    // 3. 将聚合好的设备批量加入到真正的 UI 数据源中
                    foreach (var device in tempDeviceDict.Values)
                {
                    DeviceConfigs.Add(device);
                }

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