using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Models;

/// <summary>
/// 命令执行状态
/// </summary>
public enum CommandStatus
{
    /// <summary>
    /// 已接收
    /// </summary>
    Received = 0,

    /// <summary>
    /// 已接受，准备执行
    /// </summary>
    Accepted = 1,

    /// <summary>
    /// 被拒绝（如权限不足、参数非法）
    /// </summary>
    Rejected = 2,

    /// <summary>
    /// 执行成功
    /// </summary>
    Succeeded = 3,

    /// <summary>
    /// 执行失败
    /// </summary>
    Failed = 4,

    /// <summary>
    /// 执行超时
    /// </summary>
    Timeout = 5
}
