using Collector.Contracts.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;

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
    }
}
