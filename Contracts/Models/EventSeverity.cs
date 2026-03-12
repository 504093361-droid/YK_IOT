using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Models;

/// <summary>
/// 事件严重级别
/// </summary>
public enum EventSeverity
{
    /// <summary>
    /// 普通信息
    /// </summary>
    Info = 0,

    /// <summary>
    /// 警告
    /// </summary>
    Warning = 1,

    /// <summary>
    /// 错误
    /// </summary>
    Error = 2,

    /// <summary>
    /// 严重错误
    /// </summary>
    Critical = 3
}