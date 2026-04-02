using Collector.Contracts;
using Collector.Contracts.Enums;
using Collector.Contracts.Model; // 🟢 确保引入 SystemOptions 所在的命名空间
using Collector.Edge.Configuration;
using Collector.Edge.Control;
using Collector.Edge.Engine;
using Collector.Edge.Messaging;
using Collector.Edge.Processing;
using Collector.Edge.Publishing;
using Contracts.Interface;
using Microsoft.Extensions.Configuration; // 🟢 确保引入 Configuration 扩展
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

// 能用10年
if (!HslCommunication.Authorization.SetAuthorizationCode("4b86f3fc-f650-3b08-5924-b0f8278d6ed2"))
{
    Console.WriteLine("激活Hsl失败");
}

// 1. 创建宿主建造者 (这里已经自动加载了 appsettings.json 并开启了热重载)
var builder = Host.CreateDefaultBuilder(args);

// 2. 注册依赖注入
builder.ConfigureServices((hostContext, services) =>
{
    // 配置日志 (为了控制台看着舒服，加点时间戳)
    services.AddLogging(configure =>
    {
        configure.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
    });

    // 🟢 新增核心：将 appsettings.json 中的 SystemOptions 节点绑定到实体类
    // 宿主会自动把 IOptionsMonitor<SystemOptions> 注入给底层需要的类库 (如 Worker)
    services.Configure<SystemOptions>(hostContext.Configuration.GetSection("SystemOptions"));

    // 注册 Configuration 层 (单例：保证全局拿到的都是同一份配置)
    services.AddSingleton<IConfigManager, ConfigManager>();

    services.AddSingleton<IDataProcessor, DataProcessor>();
    // 注册 Control 层 (单例：全局大脑)
    services.AddSingleton<IEdgeController, EdgeController>();

    // 这行代码必须有，确保全 Edge 共享这唯一的一个 MQTT 连接！
    services.AddSingleton<IMqttService, EdgeMqttService>();

    services.AddSingleton<IMqttPublisher, MqttPublisher>();
    // 注册车间主任 (单例，统管全局 Worker)
    services.AddSingleton<ICollectionEngine, CollectionEngine>();

    // 注册 Messaging 层 (作为后台长驻服务跑起来)
    services.AddHostedService<MqttCommandReceiver>();
});

// 3. 构建并运行
var app = builder.Build();
await app.RunAsync();