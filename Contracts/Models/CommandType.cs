using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Models;

/// <summary>
/// 命令类型
/// </summary>
public enum CommandType
{
    /// <summary>
    /// 设置参数
    /// 例如温度设定值、压力设定值、速度设定值
    /// </summary>
    SetParameter = 0,

    /// <summary>
    /// 启动设备
    /// </summary>
    Start = 1,

    /// <summary>
    /// 停止设备
    /// </summary>
    Stop = 2,

    /// <summary>
    /// 复位设备
    /// </summary>
    Reset = 3
}
