


using Collector.Contracts;
using Collector.Contracts.Enums;
using Collector.Edge.Configuration;
using Collector.Edge.Control;
using Collector.Edge.Engine;
using Collector.Edge.Messaging;
using Collector.Edge.Processing;
using Collector.Edge.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;



//能用10年
if (!HslCommunication.Authorization.SetAuthorizationCode("4b86f3fc-f650-3b08-5924-b0f8278d6ed2"))
{
    Console.WriteLine("激活Hsl失败");
}

// 1. 创建宿主建造者
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

    // 注册 Configuration 层 (单例：保证全局拿到的都是同一份配置)
    services.AddSingleton<IConfigManager, ConfigManager>();

    services.AddSingleton<IDataProcessor, DataProcessor> ();
    // 注册 Control 层 (单例：全局大脑)
    services.AddSingleton<IEdgeController, EdgeController>();

    services.AddSingleton<IMqttPublisher, MqttPublisher>();
    // 🟢 新增：注册车间主任 (单例，统管全局 Worker)
    services.AddSingleton<ICollectionEngine, CollectionEngine>();

    // 注册 Messaging 层 (作为后台长驻服务跑起来)
    services.AddHostedService<MqttCommandReceiver>();
});

// 3. 构建并运行
var app = builder.Build();

await app.RunAsync();

