using System;
using System.Collections.Generic;
using System.Text;

using Collector.Contracts;
using System.Collections.Generic;
using System.Linq;

namespace Collector.Edge.Configuration
{
    public class ConfigManager : IConfigManager
    {
        private List<DeviceConfig> _currentConfigs = new();
        private readonly object _configLock = new();

        public void UpdateConfig(List<DeviceConfig> newConfigs)
        {
            lock (_configLock)
            {
                // 创建一个全新副本，防止外部意外修改内存引用
                _currentConfigs = newConfigs?.ToList() ?? new List<DeviceConfig>();
            }
        }

        public List<DeviceConfig> GetCurrentConfigs()
        {
            lock (_configLock)
            {
                // 返回副本，保护内部数据不被脏读/脏写
                return _currentConfigs.ToList();
            }
        }
    }
}
