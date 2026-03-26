using System;
using System.Collections.Generic;
using System.Text;

namespace Collector.Edge.Control
{
    public interface IEdgeController
    {
        Task HandleConfigUpdatedAsync(string configJson);

        // 🟢 补全：向外界暴露“紧急制动”的接口
        Task StopEngineAsync();
    }
}
