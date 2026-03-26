using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Interface
{
    public interface IMqttService
    {
        /// <summary>
        /// 异步发布 MQTT 消息
        /// </summary>
        /// <param name="topic">主题</param>
        /// <param name="payload">消息载荷 (JSON)</param>
        /// <param name="retain">是否作为保留消息</param>
        /// <returns>IsSuccess: 是否成功, ErrorMessage: 错误信息</returns>
        Task<(bool IsSuccess, string ErrorMessage)> PublishAsync(string topic, string payload, bool retain = false);


        // 🟢 2. 新增：订阅方法
        Task SubscribeAsync(string topic);

        // 🟢 3. 新增：接收消息事件
        event Func<string, string, Task> OnMessageReceived;
    }
}
