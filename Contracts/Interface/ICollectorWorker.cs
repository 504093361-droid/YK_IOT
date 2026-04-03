using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Interface
{
    public interface ICollectWorker
    {
      

        // 为了适配全异步驱动，将 Start 升级为 Task
        Task StartAsync();

        Task StopAsync();
    }
}
