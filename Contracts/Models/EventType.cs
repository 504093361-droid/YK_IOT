using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Models;

/// <summary>
/// 事件类型
/// 表示“发生了什么事”
/// </summary>
public enum EventType
{
    /// <summary>
    /// 报警事件
    /// </summary>
    Alarm = 0,

    /// <summary>
    /// 状态变化事件
    /// 例如设备从 Idle 变为 Running
    /// </summary>
    StatusChanged = 1,

    /// <summary>
    /// 命令执行成功事件
    /// </summary>
    CommandExecuted = 2,

    /// <summary>
    /// 命令执行失败事件
    /// </summary>
    CommandFailed = 3,

    /// <summary>
    /// 阈值超限事件
    /// 例如温度超限、压力超限
    /// </summary>
    ThresholdExceeded = 4,

    /// <summary>
    /// 业务事件
    /// 例如工单开始、工单结束、批次切换
    /// </summary>
    BusinessEvent = 5
}