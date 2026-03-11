using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Models;

/// <summary>
/// 命令消息
/// 表示上层系统向现场设备/边缘层下发的一条控制命令
/// </summary>
public class CommandMessage
{
    /// <summary>
    /// 命令唯一标识
    /// 用于追踪、去重、回执关联
    /// </summary>
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 目标设备编号
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 命令类型
    /// </summary>
    public CommandType CommandType { get; set; }

    /// <summary>
    /// 参数名称
    /// 当命令类型为 SetParameter 时使用，例如 "TemperatureSetpoint"
    /// </summary>
    public string ParameterName { get; set; } = string.Empty;

    /// <summary>
    /// 参数值
    /// 为了保持通用性，先用字符串承载，后续可以按业务解析
    /// </summary>
    public string ParameterValue { get; set; } = string.Empty;

    /// <summary>
    /// 命令来源
    /// 例如 MES / AI / HMI / WMS
    /// </summary>
    public string SourceSystem { get; set; } = string.Empty;

    /// <summary>
    /// 下发时间戳（Unix 毫秒）
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>
    /// 超时时间（毫秒）
    /// 如果超过这个时间还没有成功执行，应视为超时
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;
}
