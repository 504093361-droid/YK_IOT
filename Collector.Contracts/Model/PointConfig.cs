using Collector.Contracts.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Collector.Contracts
{
    public partial class PointConfig : ObservableObject
    {
        [ObservableProperty]
        private string pointId = Guid.NewGuid().ToString("N")[..8];

        [ObservableProperty]
        private string pointName = "新建点位";

        [ObservableProperty]
        private string address = "40001";

        [ObservableProperty]
        private DataTypeEnum dataType = DataTypeEnum.Float;

        [ObservableProperty]
        private ushort _length = 10;


        // 🟢 新增：线性转换因子 (y = kx + b)
        [ObservableProperty]
        private double _multiplier = 1.0; // 比例 (k)

        [ObservableProperty]
        private double _offset = 0.0;     // 偏移 (b)


        [ObservableProperty]
        private string _expression = string.Empty; // 动态公式 (例如: "x * 2 + Sin(x)")

        // 👇 观察窗专属字段 👇

        [property: JsonIgnore]
        [ObservableProperty]
        private object? _rawValue;       // 🟢 纯粹的原始电信号 (Raw)

        [property: JsonIgnore]
        [ObservableProperty]
        private object? _processedValue; // 🟢 业务关心的真实物理量 (Processed)


        [property: JsonIgnore]
        [ObservableProperty]
        private object? _currentValue; // 最近一次采集值

        [property: JsonIgnore]
        [ObservableProperty]
        private DateTime? _lastUpdateTime; // 最近一次采集时间

        [property: JsonIgnore]
        [ObservableProperty]
        private bool _isSuccess = true; // 采集是否成功 (UI 可据此绑个转换器把背景变红)

        [property: JsonIgnore]
        [ObservableProperty]
        private string _errorMessage = string.Empty; // 最近异常信息

    }
}
