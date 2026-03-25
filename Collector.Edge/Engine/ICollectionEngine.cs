using System;
using System.Collections.Generic;
using System.Text;

namespace Collector.Edge.Engine
{
    public interface ICollectionEngine
    {
        // 启动引擎（根据当前配置拉起所有 Worker）
        Task StartAsync();

        // 停止引擎（一键停止所有 Worker）
        Task StopAsync();

        // 热重载（当收到新配置时：先全停，再全启）
        Task ReloadAsync();
    }


}
