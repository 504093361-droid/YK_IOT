// 1. 初始化 Serilog
using Collector.Contracts;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// 2. 注册核心采集引擎为单例
builder.Services.AddSingleton<CollectionEngine>();

var app = builder.Build();

// 3. 定义接收 WPF 传过来配置的 API 接口
app.MapPost("/api/config/apply", (List<DeviceConfig> configs, CollectionEngine engine) =>
{
    Log.Information("📥 接收到前端 UI 下发的最新配置，共 {Count} 个设备", configs.Count);

    // 触发引擎重启/加载新配置
    engine.ApplyConfigAndStart(configs);

    return Results.Ok(new { Message = "配置已成功接收，采集任务已调度" });
});

Log.Information("🚀 采集服务端已启动，监听端口 5000...");
app.Run("http://localhost:5000");