using System;
using System.Collections.Generic;
using System.Text;

namespace Collector.Contracts.Model
{
    // 🟢 系统的全局大管家
    public class SystemOptions
    {
        // worker 重试与熔断机制
        public int MaxRetryCount { get; set; } = 3;            // 触发熔断的连续失败次数

        public int HalfRetryCount { get; set; } = 2;  // 熔断半开状态允许的重试次数（成功则恢复，失败则继续熔断）
        public int CircuitBreakerDelayMs { get; set; } = 30000; // 熔断隔离的休眠时间(毫秒)
        public int StartupBypassCount { get; set; } = 3;       // 新兵免检期次数

        public int PointMaxRetryBeforeQuarantine { get; set; } = 3;   // 点位连续错几次关禁闭
        public int PointQuarantineDurationSeconds { get; set; } = 60; // 禁闭时长


        // 心跳与保活
        public int HeartbeatIntervalSeconds { get; set; } = 30; // 强制上报心跳的时间间隔(秒)

        //UI用的生死线
        public int HeartbeatLimit { get; set; } = 60; // 强制上报心跳的时间间隔(秒)
        // 其他未来的魔法数字可以无限往这里面塞...
    }
}
