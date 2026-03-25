using Collector.Contracts;
using System;
using System.Collections.Generic;
using System.Text;

namespace Collector.Edge.Configuration
{
    public interface IConfigManager
    {
        void UpdateConfig(List<DeviceConfig> newConfigs);
        List<DeviceConfig> GetCurrentConfigs();
    }
}
