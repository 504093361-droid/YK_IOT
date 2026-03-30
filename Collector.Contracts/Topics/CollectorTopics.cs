using System;
using System.Collections.Generic;
using System.Text;

namespace Collector.Contracts.Topics
{
    public static class CollectorTopics
    {
        // 1. 根节点统一定义，方便以后一键修改前缀
        private const string Root = "scada/collector";

        // 2. 静态常量：用于全局固定的主题 (如：配置下发)
        // 实际值为: scada/collector/config/update
        public const string ConfigUpdate = $"{Root}/config/update";

        // 3. 动态模板：通过方法生成针对特定设备的主题 (如：设备实时数据流)
        // 实际值为: scada/collector/data/PLC_01/raw
        public static string GetDeviceStandDataTopic(string deviceId)
            => $"{Root}/data/{deviceId}/standard";

        // 4. 设备在线状态主题 (遗嘱消息/上下线通知)
        // 实际值为: scada/collector/status/PLC_01
        public static string GetDeviceStatusTopic(string deviceId)
            => $"{Root}/status/{deviceId}";

    

        // 🟢 新增：专门用于控制引擎启停的指令频道
        public const string EngineControl = "scada/engine/control";
    }
}
