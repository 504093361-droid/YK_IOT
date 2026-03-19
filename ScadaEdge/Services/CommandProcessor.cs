using System;
using System.Collections.Generic;
using System.Text;

using Contracts.Models;

namespace ScadaEdge.Services;

/// <summary>
/// 命令处理器
/// 当前版本先做最小模拟：
/// 1. 接收命令
/// 2. 做基础校验
/// 3. 模拟执行
/// 4. 返回执行结果
/// </summary>
using Contracts.Models;


public static class CommandProcessor
{
    /// <summary>
    /// 处理命令，并返回命令执行结果
    /// </summary>
    public static CommandResultMessage Handle(CommandMessage command)
    {
        var result = new CommandResultMessage
        {
            CommandId = command.CommandId,
            DeviceId = command.DeviceId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SourceSystem = command.SourceSystem
        };

        if (string.IsNullOrWhiteSpace(command.DeviceId))
        {
            result.Status = CommandStatus.Rejected;
            result.Message = "DeviceId 不能为空。";
            return result;
        }

        switch (command.CommandType)
        {
            case CommandType.SetParameter:
                if (string.IsNullOrWhiteSpace(command.ParameterName))
                {
                    result.Status = CommandStatus.Rejected;
                    result.Message = "SetParameter 命令缺少 ParameterName。";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(command.ParameterValue))
                {
                    result.Status = CommandStatus.Rejected;
                    result.Message = "SetParameter 命令缺少 ParameterValue。";
                    return result;
                }

                result.Status = CommandStatus.Succeeded;
                result.Message = $"参数 {command.ParameterName} 已设置为 {command.ParameterValue}";
                return result;

            case CommandType.Start:
                result.Status = CommandStatus.Succeeded;
                result.Message = "设备启动命令已执行。";
                return result;

            case CommandType.Stop:
                result.Status = CommandStatus.Succeeded;
                result.Message = "设备停止命令已执行。";
                return result;

            case CommandType.Reset:
                result.Status = CommandStatus.Succeeded;
                result.Message = "设备复位命令已执行。";
                return result;

            default:
                result.Status = CommandStatus.Failed;
                result.Message = "不支持的命令类型。";
                return result;
        }
    }

    /// <summary>
    /// 根据命令及结果生成事件消息
    /// </summary>
    public static EventMessage CreateCommandEvent(CommandMessage command, CommandResultMessage result)
    {
        var eventType = result.Status == CommandStatus.Succeeded
            ? EventType.CommandExecuted
            : EventType.CommandFailed;

        var severity = result.Status == CommandStatus.Succeeded
            ? EventSeverity.Info
            : EventSeverity.Error;

        return new EventMessage
        {
            EventId = Guid.NewGuid().ToString("N"),
            Namespace = $"event/site1/line1/{command.DeviceId}",
            DeviceId = command.DeviceId,
            EventType = eventType,
            Severity = severity,
            EventName = command.CommandType.ToString(),
            Message = result.Message,
            SourceSystem = "ScadaEdge",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RelatedCommandId = command.CommandId
        };
    }
}
