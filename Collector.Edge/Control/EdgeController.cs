using Collector.Contracts;
using Collector.Edge.Configuration;
using Collector.Edge.Engine;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Security.Cryptography; // 新增：用于计算哈希
using System.Text;                  // 新增：用于字符串转换
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Collector.Edge.Control
{
    public class EdgeController : IEdgeController
    {
        private readonly IConfigManager _configManager;
        private readonly ILogger<EdgeController> _logger;
        private readonly ICollectionEngine _engine; // 核心新增：注入车间主任

        // 核心新增：记忆上一次成功应用的配置哈希值
        private string _lastConfigHash = string.Empty;

        public EdgeController(IConfigManager configManager, ILogger<EdgeController> logger, ICollectionEngine engine)
        {
            _configManager = configManager;
            _engine = engine;
            _logger = logger;
        }

        public  async Task HandleConfigUpdatedAsync(string configJson)
        {
            try
            {
                // 1. 计算新传入 JSON 的哈希值
                string newHash = CalculateHash(configJson);

                // 2. 判断是否与当前配置完全一致
                if (newHash == _lastConfigHash)
                {
                    _logger.LogInformation("💡 收到配置推送，但经比对与当前运行配置完全一致，已忽略本次动作。");
                    return;
                }

                _logger.LogInformation("⚙️ 检测到配置发生实质性变更，准备解析并更新底层引擎...");

                // 3. 解析配置
                var options = new JsonSerializerOptions();
                options.Converters.Add(new JsonStringEnumConverter());
                var configs = JsonSerializer.Deserialize<List<DeviceConfig>>(configJson, options);

                if (configs != null)
                {
                    // 4. 更新配置中心
                    _configManager.UpdateConfig(configs);

                    // 5. 只有在真正解析并更新成功后，才把新哈希记下来
                    _lastConfigHash = newHash;

                    _logger.LogInformation("✅ 配置应用成功！当前载入设备数量: {Count}", configs.Count);

                    // 通知 Engine 层重载/启动采集任务
                    await _engine.ReloadAsync(); 
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 解析配置失败！格式不合法，忽略本次更新。");
            }

        }


        public async Task StopEngineAsync()
        {
            // 1. 调用 CollectionEngine 停止所有在跑的 Worker 线程
            await _engine.StopAllAsync(); // 假设你之前写了这个方法，它内部调用了 worker.Stop() 和 cts.Cancel()

            // 2. 🚨 极其关键的一步：清空配置指纹！
            // 这样下次 UI 再次下发相同的配置时，哈希比对才会通过，引擎才会重新拉起！
            _lastConfigHash = string.Empty;

            _logger.LogInformation("✅ 所有采集任务已停止，引擎进入待命状态。");

          
        }

        // 辅助方法：计算字符串的 MD5 哈希
        private string CalculateHash(string input)
        {
            using var md5 = MD5.Create();
            byte[] inputBytes = Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = md5.ComputeHash(inputBytes);
            // .NET 5+ 推荐使用 Convert.ToHexString 快速转成大写十六进制字符串
            return Convert.ToHexString(hashBytes);
        }
    }
}