using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Topics
{
    public static class MqttTopics
    {
        public const string CollectorStatus = "status/collector";
        public const string CommandDeviceWrite = "command/device/write";
    }
}
