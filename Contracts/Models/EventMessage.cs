using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Models;

/// <summary>
/// 事件消息
/// 表示系统中某件已经发生的事情
/// </summary>
public class EventMessage
{
    /// <summary>
    /// 事件唯一标识
    /// 用于追踪和审计
    /// </summary>
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 命名空间路径
    /// 用于表达该事件在 UNS 中的位置
    /// </summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// 设备编号
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 事件类型
    /// </summary>
    public EventType EventType { get; set; }

    /// <summary>
    /// 事件严重级别
    /// </summary>
    public EventSeverity Severity { get; set; }

    /// <summary>
    /// 事件名称
    /// 例如 TemperatureHigh / MotorStarted / CommandExecuted
    /// </summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>
    /// 事件描述
    /// 用于补充说明
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 来源系统
    /// 例如 ScadaEdge / MES / AI / HMI
    /// </summary>
    public string SourceSystem { get; set; } = string.Empty;

    /// <summary>
    /// 事件时间戳（Unix 毫秒）
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>
    /// 可选关联命令 ID
    /// 如果此事件由某个命令触发，可以记录命令编号
    /// </summary>
    public string RelatedCommandId { get; set; } = string.Empty;
}
