
using Collector.Contracts;

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
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Collector.UI.ViewModel
{
    public partial class Page1ViewModel : ObservableRecipient
    {
        private readonly ILogger _logger;
        private readonly string _configFilePath = "ScadaConfig.json";

        private readonly IMqttService _mqttService; // 注入提取出来的服务

        [ObservableProperty]
        private ObservableCollection<DeviceConfig> deviceConfigs = new();

        // 追踪当前选中的设备
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDeviceSelected))] // 当选中项改变时，通知界面更新 IsDeviceSelected 状态
        private DeviceConfig? selectedDevice;

        // 用于控制下方点位面板是否可用
        public bool IsDeviceSelected => SelectedDevice is not null;

        public Page1ViewModel(ILogger logger, IMqttService mqttService)
        {
            _logger = logger;
            _mqttService = mqttService;

        }

        #region 设备操作 Commands

        [RelayCommand]
        private void AddDevice()
        {
            var newDevice = new DeviceConfig { DeviceName = $"设备_{DeviceConfigs.Count + 1}" };
            DeviceConfigs.Add(newDevice);
            SelectedDevice = newDevice; // 自动选中新添加的设备
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
                options.Converters.Add(new JsonStringEnumConverter()); // 加上这句，枚举就会存成 "ModbusTCP" 这样的字符串
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

            // 1. 序列化配置
            var options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter());
            string configJson = JsonSerializer.Serialize(DeviceConfigs, options);

            // 2. 调用独立服务进行下发 (使用规范中约定的 scada/config/update 主题)
            var (isSuccess, errorMessage) = await _mqttService.PublishAsync(
                topic: CollectorTopics.ConfigUpdate,
                payload: configJson,
                retain: true);

            // 3. 处理 UI 响应
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

        #endregion

        // 记得引入 using System.IO; 和 using System.Text.Json;

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

                // 核心修改：实例化 Options 并添加枚举转换器
                var options = new JsonSerializerOptions();
                options.Converters.Add(new JsonStringEnumConverter());

                // 反序列化时，把 options 作为第二个参数传进去
                var loadedConfigs = JsonSerializer.Deserialize<ObservableCollection<DeviceConfig>>(jsonString, options);

                if (loadedConfigs != null)
                {
                    DeviceConfigs = loadedConfigs;

                    // 清空当前选中项，避免越界或显示异常
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
    }
}