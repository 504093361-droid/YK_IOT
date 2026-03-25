using System;
using System.Collections.Generic;
using System.Text;

namespace Collector.Edge.Control
{
    public interface IEdgeController
    {
        Task HandleConfigUpdatedAsync(string configJson);
    }
}
