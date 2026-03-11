using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Models;

/// <summary>
/// 命令执行结果消息
/// 用于对命令的接收、执行结果进行回执
/// </summary>
public class CommandResultMessage
{
    /// <summary>
    /// 原始命令 ID
    /// 用于与 CommandMessage 关联
    /// </summary>
    public string CommandId { get; set; } = string.Empty;

    /// <summary>
    /// 目标设备编号
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 执行状态
    /// </summary>
    public CommandStatus Status { get; set; }

    /// <summary>
    /// 回执时间戳（Unix 毫秒）
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>
    /// 结果描述
    /// 例如“参数已下发成功”“设备未就绪”“超时未响应”
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 命令来源系统
    /// 便于审计追踪
    /// </summary>
    public string SourceSystem { get; set; } = string.Empty;
}
