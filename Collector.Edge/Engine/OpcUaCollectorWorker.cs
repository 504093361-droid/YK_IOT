using Collector.Contracts;
using Collector.Contracts.Model;
using Collector.Edge.Processing;
using Collector.Edge.Publishing;
using Contracts.Interface;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Collector.Edge.Engine
{
    /// <summary>
    /// OPC UA 标准型采集 Worker
    ///
    /// 本版目标：
    /// 1. 保留现有 Processor / Publisher / Point.Handle 处理链，不破坏现有架构
    /// 2. 启动失败后，不直接退出，而是在后台循环重试
    /// 3. 运行中断线时，继续使用 KeepAlive + SessionReconnectHandler 自动重连
    /// 4. 重连完成后，重新收紧新 Session 的属性和事件绑定，消除潜在隐患
    ///
    /// 适配目标：OPCFoundation.NetStandard.Opc.Ua 1.5.378.134
    /// </summary>
    public class OpcUaCollectWorker : ICollectWorker
    {
        private readonly DeviceConfig _device;
        private readonly IMqttPublisher _publisher;
        private readonly ILogger _logger;
        private readonly IDataProcessor _processor;
        private readonly IOptionsMonitor<SystemOptions> _sysOptions;

        // 官方当前路线引入的 Telemetry
        // 真正最佳实践是由 Host 根部统一注入；
        // 这里为了保持 Worker 可独立落地，先在本类中自持一个静态上下文。
        private static readonly ITelemetryContext s_telemetry = DefaultTelemetry.Create(_ => { });

        // 保护生命周期的锁：避免并发 Start/Stop 抢占资源
        private readonly object _syncRoot = new object();
        private readonly SemaphoreSlim _lifecycleLock = new SemaphoreSlim(1, 1);

        // OPC UA 应用级对象
        private ApplicationConfiguration _appConfiguration;
        private ApplicationInstance _application;

        // 运行期核心对象
        private ISession _opcSession;
        private Subscription _subscription;
        private SessionReconnectHandler _reconnectHandler;
        private CancellationTokenSource _workerCts;

        // 启动失败后台重试任务
        private Task _startRetryTask;

        // 状态标记
        private volatile bool _isStopping;
        private volatile bool _startRequested;

        // 运行期参数
        private const int DefaultKeepAliveInterval = 5000;
        private const int DefaultReconnectPeriod = 5000;
        private const int DefaultReconnectExponentialBackoff = 15000;
        private const int DefaultSessionTimeout = 60000;

        // 启动失败后的后台重试参数
        private const int InitialStartRetryDelayMs = 3000;   // 首次失败后 3 秒重试
        private const int MaxStartRetryDelayMs = 30000;      // 最大退避到 30 秒
        private const int MinSamplingIntervalMs = 100;       // 防止扫描周期过小

        public OpcUaCollectWorker(
            DeviceConfig device,
            IMqttPublisher publisher,
            ILogger logger,
            IDataProcessor processor,
            IOptionsMonitor<SystemOptions> sysOptions)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _sysOptions = sysOptions ?? throw new ArgumentNullException(nameof(sysOptions));

            _workerCts = new CancellationTokenSource();
        }

        /// <summary>
        /// 启动 Worker。
        ///
        /// 注意：本方法现在不会“等待连接成功”才返回，
        /// 而是改成：
        /// 1. 标记启动请求已发出
        /// 2. 启动后台重试任务
        /// 3. 立即返回给上层
        ///
        /// 这样即便车间 OPC Server 尚未启动，Worker 也不会直接报死，
        /// 而是持续在后台尝试连接。
        /// </summary>
        public async Task StartAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // 已经在线：忽略重复启动
                if (_opcSession != null && _opcSession.Connected)
                {
                    _logger.LogWarning("OPC UA 设备 [{DeviceName}] 已处于运行状态，忽略重复启动。", _device.DeviceName);
                    return;
                }

                // 已经存在启动重试任务，并且仍在运行：忽略重复启动
                if (IsStartRetryTaskRunning())
                {
                    _logger.LogWarning("OPC UA 设备 [{DeviceName}] 启动重试任务已在运行，忽略重复启动。", _device.DeviceName);
                    return;
                }

                _isStopping = false;
                _startRequested = true;

                // 如果 CTS 已被取消，则重建一个新的 CTS
                if (_workerCts == null || _workerCts.IsCancellationRequested)
                {
                    _workerCts?.Dispose();
                    _workerCts = new CancellationTokenSource();
                }

                _logger.LogInformation("收到 OPC UA 设备 [{DeviceName}] 启动请求，开始后台连接/重试流程。", _device.DeviceName);

                // 先给 UI / 状态面板一个“启动中”反馈
                await PublishStatusSafeAsync("启动中/连接中(OPC UA)", 0).ConfigureAwait(false);

                // 启动后台重试任务
                // 注意：这里故意不 await，让它在后台循环，StartAsync 本身快速返回。
                _startRetryTask = Task.Run(
                    () => StartWithRetryLoopAsync(_workerCts.Token),
                    CancellationToken.None);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        /// <summary>
        /// 停止 Worker。
        ///
        /// 这里会：
        /// 1. 取消后台启动重试
        /// 2. 等待后台任务优雅结束
        /// 3. 关闭 Session / Subscription / 解绑事件
        /// </summary>
        public async Task StopAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _isStopping = true;
                _startRequested = false;

                _logger.LogInformation("正在停止 OPC UA 设备: [{DeviceName}]", _device.DeviceName);

                try
                {
                    _workerCts?.Cancel();
                }
                catch
                {
                    // ignore
                }

                // 等待后台启动重试任务结束，避免它和 Stop/Cleanup 交叉运行
                if (_startRetryTask != null)
                {
                    try
                    {
                        await _startRetryTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 正常情况：Stop 导致后台任务取消
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "等待 OPC UA 启动重试后台任务结束时发生异常。");
                    }
                    finally
                    {
                        _startRetryTask = null;
                    }
                }

                await CleanupResourcesAsync(CancellationToken.None).ConfigureAwait(false);
                await PublishStatusSafeAsync("已停止", 0).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OPC UA 停止/释放资源失败");
            }
            finally
            {
                _workerCts?.Dispose();
                _workerCts = new CancellationTokenSource();
                _lifecycleLock.Release();
            }
        }

        /// <summary>
        /// 后台启动重试总循环。
        ///
        /// 逻辑：
        /// - 只要还没 Stop，并且还没成功建立连接，就一直尝试
        /// - 每次失败后做清理，再等待一段时间重试
        /// - 采用简单退避：3s -> 6s -> 12s -> ... -> 最多 30s
        /// </summary>
        private async Task StartWithRetryLoopAsync(CancellationToken ct)
        {
            int attempt = 0;
            int retryDelayMs = InitialStartRetryDelayMs;

            while (!ct.IsCancellationRequested && !_isStopping && _startRequested)
            {
                attempt++;

                try
                {
                    _logger.LogInformation(
                        "OPC UA 设备 [{DeviceName}] 开始第 {Attempt} 次启动尝试。",
                        _device.DeviceName,
                        attempt);

                    await StartCoreOnceAsync(ct).ConfigureAwait(false);

                    // 成功连接并建立订阅后，直接退出后台循环
                    _logger.LogInformation(
                        "OPC UA 设备 [{DeviceName}] 在第 {Attempt} 次尝试后启动成功。",
                        _device.DeviceName,
                        attempt);

                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || _isStopping)
                {
                    _logger.LogInformation("OPC UA 设备 [{DeviceName}] 启动重试任务收到取消信号，准备退出。", _device.DeviceName);
                    return;
                }
                catch (Exception ex)
                {
                    // 启动失败：先记录日志和状态，再清理残留资源，最后等待后重试
                    _logger.LogError(
                        ex,
                        "OPC UA 设备 [{DeviceName}] 第 {Attempt} 次启动失败，将在 {RetryDelayMs} ms 后重试。",
                        _device.DeviceName,
                        attempt,
                        retryDelayMs);

                    await PublishStatusSafeAsync(
                        $"启动失败，{retryDelayMs / 1000.0:F0}s后重试: {ex.Message}",
                        -1).ConfigureAwait(false);

                    await CleanupResourcesAsync(CancellationToken.None).ConfigureAwait(false);

                    if (ct.IsCancellationRequested || _isStopping || !_startRequested)
                    {
                        return;
                    }

                    try
                    {
                        await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    // 简单指数退避，但封顶
                    retryDelayMs = Math.Min(retryDelayMs * 2, MaxStartRetryDelayMs);
                }
            }
        }

        /// <summary>
        /// 单次启动核心流程。
        ///
        /// 它只负责“尝试一次”：
        /// 1. 应用级初始化
        /// 2. 建立 Session
        /// 3. 配置 Session 的 KeepAlive / 重连属性
        /// 4. 建立 Subscription
        /// 5. 创建所有监控项
        ///
        /// 如果任何一步失败，异常向上抛给后台重试循环。
        /// </summary>
        private async Task StartCoreOnceAsync(CancellationToken ct)
        {
            await EnsureApplicationBootstrapAsync(ct).ConfigureAwait(false);

            // 1. 建立 Session
            ISession session = await ConnectToServerAsync(_device, ct).ConfigureAwait(false);

            if (session == null || !session.Connected)
            {
                throw new Exception("无法建立 OPC UA Session。");
            }

            // 2. Session 接入到 Worker
            // 注意：这里单独抽成 ConfigureConnectedSession，
            // 目的是在首次连接、重连成功接管新 Session 时都复用同一套收紧逻辑。
            ConfigureConnectedSession(session);

            // 3. 准备重连处理器
            lock (_syncRoot)
            {
                _reconnectHandler?.Dispose();
                _reconnectHandler = new SessionReconnectHandler(
                    s_telemetry,
                    reconnectAbort: true,
                    maxReconnectPeriod: DefaultReconnectExponentialBackoff);
            }

            // 4. 创建订阅
            _subscription = new Subscription(_opcSession.DefaultSubscription)
            {
                DisplayName = $"{_device.DeviceName}_Subscription",
                PublishingInterval = Math.Max(_device.ScanIntervalMs, MinSamplingIntervalMs),
                KeepAliveCount = 10,
                LifetimeCount = 100,
                PublishingEnabled = true,
                Priority = 1
            };

            _opcSession.AddSubscription(_subscription);
            await _subscription.CreateAsync(ct).ConfigureAwait(false);

            // 5. 添加监控项
            if (_device.Points != null)
            {
                foreach (var point in _device.Points)
                {
                    if (point == null)
                    {
                        continue;
                    }

                    NodeId nodeId;
                    try
                    {
                        nodeId = NodeId.Parse(point.Address);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "点位 [{PointName}] 地址非法，已跳过。Address={Address}", point.PointName, point.Address);
                        continue;
                    }

                    var item = new MonitoredItem(_subscription.DefaultItem)
                    {
                        DisplayName = point.PointName,
                        StartNodeId = nodeId,
                        AttributeId = Attributes.Value,
                        SamplingInterval = Math.Max(_device.ScanIntervalMs, MinSamplingIntervalMs),
                        QueueSize = 1,
                        DiscardOldest = true,

                        // 保留你现有架构：把 PointConfig 直接放进 Handle，供回调链复用
                        Handle = point
                    };

                    // 保留你现有逻辑：按点位配置决定是否挂 DataChangeFilter
                    if (point.Deadband > 0)
                    {
                        item.Filter = new DataChangeFilter
                        {
                            Trigger = DataChangeTrigger.StatusValue,
                            DeadbandType = (uint)DeadbandType.Absolute,
                            DeadbandValue = point.Deadband
                        };
                    }

                    item.Notification += OnDataChanged;
                    _subscription.AddItem(item);
                }
            }

            await _subscription.ApplyChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation("OPC UA 设备 [{DeviceName}] 启动成功，订阅已建立。", _device.DeviceName);
            await PublishStatusSafeAsync("在线/采集中(OPC UA)", 1).ConfigureAwait(false);
        }

        /// <summary>
        /// 应用级初始化。
        ///
        /// 这里仍然保持“只初始化一次”的策略，不破坏你现有架构。
        /// 后续无论启动失败重试多少次，都复用同一个 ApplicationConfiguration / ApplicationInstance。
        /// </summary>
        private async Task EnsureApplicationBootstrapAsync(CancellationToken ct)
        {
            if (_appConfiguration != null && _application != null)
            {
                return;
            }

            var config = new ApplicationConfiguration(s_telemetry)
            {
                ApplicationName = "Youkai_Scada_Client",
                ApplicationUri = Utils.Format("urn:{0}:Youkai_Scada_Client", Dns.GetHostName()),
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = @"%CommonApplicationData%\OPC Foundation\CertificateStores\MachineDefault",
                        SubjectName = "CN=Youkai_Scada_Client"
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = @"%CommonApplicationData%\OPC Foundation\CertificateStores\UA Certificate Authorities"
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = @"%CommonApplicationData%\OPC Foundation\CertificateStores\UA Applications"
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = @"%CommonApplicationData%\OPC Foundation\CertificateStores\RejectedCertificates"
                    },
                    AutoAcceptUntrustedCertificates = true
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas
                {
                    OperationTimeout = 15000
                },
                ClientConfiguration = new ClientConfiguration
                {
                    DefaultSessionTimeout = DefaultSessionTimeout
                }
            };

            await config.Validate(ApplicationType.Client).ConfigureAwait(false);

            config.CertificateValidator.CertificateValidation += OnCertificateValidation;

            var application = new ApplicationInstance(config, s_telemetry)
            {
                ApplicationName = config.ApplicationName,
                ApplicationType = ApplicationType.Client
            };

            bool haveAppCertificate = await application
                .CheckApplicationInstanceCertificatesAsync(false, ct: ct)
                .ConfigureAwait(false);

            if (!haveAppCertificate)
            {
                throw new ServiceResultException(StatusCodes.BadConfigurationError, "OPC UA 客户端应用证书无效。");
            }

            _appConfiguration = config;
            _application = application;
        }

        /// <summary>
        /// 使用官方当前 async 主路径连接。
        /// </summary>
        private async Task<ISession> ConnectToServerAsync(DeviceConfig device, CancellationToken ct)
        {
            string endpointUrl = BuildEndpointUrl(device);
            _logger.LogInformation("正在连接 OPC UA Endpoint: {Url}", endpointUrl);

            IUserIdentity identity = BuildUserIdentity(device);

            EndpointDescription endpointDescription = await CoreClientUtils
                .SelectEndpointAsync(
                    _appConfiguration,
                    endpointUrl,
                    useSecurity: false,
                    s_telemetry,
                    ct)
                .ConfigureAwait(false);

            var endpointConfiguration = EndpointConfiguration.Create(_appConfiguration);
            var configuredEndpoint = new ConfiguredEndpoint(
                null,
                endpointDescription,
                endpointConfiguration);

            var sessionFactory = new DefaultSessionFactory(s_telemetry);

            ISession session = await sessionFactory
                .CreateAsync(
                    _appConfiguration,
                    connection: null,
                    endpoint: configuredEndpoint,
                    updateBeforeConnect: true,
                    checkDomain: false,
                    sessionName: "Scada_Session_" + device.DeviceId,
                    sessionTimeout: DefaultSessionTimeout,
                    identity: identity,
                    preferredLocales: default,
                    ct: ct)
                .ConfigureAwait(false);

            if (session == null || !session.Connected)
            {
                throw new Exception("SessionFactory.CreateAsync 返回空会话或未连接会话。");
            }

            return session;
        }

        /// <summary>
        /// 将一个已建立的 Session 接入到当前 Worker。
        ///
        /// 这个方法是本次修复的关键之一：
        /// - 首次连接成功时调用一次
        /// - 自动重连接管新 Session 时也调用一次
        ///
        /// 这样可以保证：
        /// 1. KeepAliveInterval 始终是我们想要的值
        /// 2. DeleteSubscriptionsOnClose / TransferSubscriptionsOnReconnect 始终正确
        /// 3. KeepAlive 事件不会重复挂，也不会漏挂
        /// </summary>
        private void ConfigureConnectedSession(ISession session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            lock (_syncRoot)
            {
                // 如果旧 Session 存在，先解绑旧事件，避免重复订阅
                if (_opcSession != null)
                {
                    try
                    {
                        _opcSession.KeepAlive -= OnSessionKeepAlive;
                    }
                    catch
                    {
                        // ignore
                    }
                }

                _opcSession = session;

                _opcSession.KeepAliveInterval = Math.Max(DefaultKeepAliveInterval, _device.ScanIntervalMs * 3);
                _opcSession.DeleteSubscriptionsOnClose = false;
                _opcSession.TransferSubscriptionsOnReconnect = true;

                // 先减再加，保证不会重复挂事件
                _opcSession.KeepAlive -= OnSessionKeepAlive;
                _opcSession.KeepAlive += OnSessionKeepAlive;
            }
        }

        private static string BuildEndpointUrl(DeviceConfig device)
        {
            string endpointPath = string.IsNullOrWhiteSpace(device.OpcEndpointPath)
                ? string.Empty
                : device.OpcEndpointPath.Trim();

            if (!string.IsNullOrEmpty(endpointPath) && !endpointPath.StartsWith("/"))
            {
                endpointPath = "/" + endpointPath;
            }

            return $"opc.tcp://{device.IpAddress}:{device.Port}{endpointPath}";
        }

        private IUserIdentity BuildUserIdentity(DeviceConfig device)
        {
            if (!string.IsNullOrWhiteSpace(device.OpcUsername))
            {
                _logger.LogInformation("使用账号密码进行 OPC UA 登录。");
                return new UserIdentity(
                    device.OpcUsername,
                    Encoding.UTF8.GetBytes(device.OpcPassword ?? string.Empty));
            }

            _logger.LogInformation("使用匿名(Anonymous)模式登录。");
            return new UserIdentity();
        }

        /// <summary>
        /// 清理运行期资源。
        ///
        /// 注意：
        /// - 这里只清理“当前 Worker 运行态资源”
        /// - 不会把 ApplicationConfiguration / ApplicationInstance 清空
        ///   因为那是应用级资源，保留可以避免每次启动重试都重复构建
        /// </summary>
        private async Task CleanupResourcesAsync(CancellationToken ct)
        {
            lock (_syncRoot)
            {
                try
                {
                    if (_opcSession != null)
                    {
                        _opcSession.KeepAlive -= OnSessionKeepAlive;
                    }
                }
                catch
                {
                    // ignore
                }

                try
                {
                    _reconnectHandler?.Dispose();
                }
                catch
                {
                    // ignore
                }

                _reconnectHandler = null;
            }

            if (_subscription != null)
            {
                try
                {
                    foreach (var item in _subscription.MonitoredItems)
                    {
                        if (item is MonitoredItem monitoredItem)
                        {
                            monitoredItem.Notification -= OnDataChanged;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "解绑 MonitoredItem 回调时发生异常。");
                }
            }

            if (_opcSession != null)
            {
                try
                {
                    // 这里是主动关闭，因此要删除服务端订阅，避免残留
                    _opcSession.DeleteSubscriptionsOnClose = true;
                    await _opcSession.CloseAsync(true, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "关闭 OPC UA Session 时发生异常。");
                }
            }

            if (_subscription != null)
            {
                try
                {
                    _subscription.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "释放 OPC UA Subscription 时发生异常。");
                }
                finally
                {
                    _subscription = null;
                }
            }

            if (_opcSession != null)
            {
                try
                {
                    _opcSession.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "释放 OPC UA Session 时发生异常。");
                }
                finally
                {
                    _opcSession = null;
                }
            }
        }

        /// <summary>
        /// KeepAlive 回调。
        ///
        /// 这是运行期掉线自愈的入口：
        /// 一旦 KeepAlive 状态变坏，就交给 SessionReconnectHandler 进入自动重连流程。
        /// </summary>
        private void OnSessionKeepAlive(ISession session, KeepAliveEventArgs e)
        {
            try
            {
                if (_isStopping)
                {
                    return;
                }

                // 忽略过期/废弃 Session 的回调
                if (_opcSession == null || !_opcSession.Equals(session))
                {
                    return;
                }

                if (ServiceResult.IsBad(e.Status))
                {
                    _logger.LogWarning(
                        "OPC UA KeepAlive 异常，设备: {DeviceName}, Status: {Status}",
                        _device.DeviceName,
                        e.Status);

                    _ = PublishStatusSafeAsync($"通讯异常: {e.Status}", -2);

                    if (_reconnectHandler == null)
                    {
                        return;
                    }

                    lock (_syncRoot)
                    {
                        var state = _reconnectHandler.BeginReconnect(
                            _opcSession,
                            reverseConnectManager: null,
                            reconnectPeriod: DefaultReconnectPeriod,
                            callback: OnReconnectComplete);

                        if (state == SessionReconnectHandler.ReconnectState.Triggered)
                        {
                            _logger.LogInformation(
                                "设备 [{DeviceName}] 已触发 OPC UA 自动重连，重连周期 {ReconnectPeriod}ms。",
                                _device.DeviceName,
                                DefaultReconnectPeriod);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "设备 [{DeviceName}] KeepAlive 异常，当前重连状态: {State}",
                                _device.DeviceName,
                                state);
                        }
                    }

                    // 已经进入重连流程，取消本次新的 KeepAlive 请求
                    e.CancelKeepAlive = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理 OPC UA KeepAlive 回调时发生异常");
            }
        }

        /// <summary>
        /// 自动重连完成回调。
        ///
        /// 这里是本次修复的第二个关键点：
        /// 如果 SessionReconnectHandler 给我们返回了一个“新的 Session 实例”，
        /// 必须把新 Session 正式接管到 Worker，并重新配置 KeepAlive/重连属性。
        /// </summary>
        private void OnReconnectComplete(object sender, EventArgs e)
        {
            try
            {
                if (!ReferenceEquals(sender, _reconnectHandler))
                {
                    return;
                }

                lock (_syncRoot)
                {
                    if (_reconnectHandler == null)
                    {
                        return;
                    }

                    // 情况 1：返回了一个新的 Session
                    if (_reconnectHandler.Session != null)
                    {
                        if (!ReferenceEquals(_opcSession, _reconnectHandler.Session))
                        {
                            _logger.LogInformation(
                                "设备 [{DeviceName}] 已重连到新的 Session。SessionId={SessionId}",
                                _device.DeviceName,
                                _reconnectHandler.Session.SessionId);

                            ISession oldSession = _opcSession;

                            // 用统一方法重新接管新 Session，重新收紧属性和事件
                            ConfigureConnectedSession(_reconnectHandler.Session);

                            // 旧 Session 此时已不再需要，静默释放
                            Utils.SilentDispose(oldSession);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "设备 [{DeviceName}] 已重新激活原 Session。SessionId={SessionId}",
                                _device.DeviceName,
                                _reconnectHandler.Session.SessionId);

                            // 即使是原 Session 被重新激活，也再收紧一次配置
                            ConfigureConnectedSession(_reconnectHandler.Session);
                        }
                    }
                    else
                    {
                        // 情况 2：没有新 Session，只是 KeepAlive 自己恢复了
                        _logger.LogInformation("设备 [{DeviceName}] KeepAlive 已恢复。", _device.DeviceName);

                        if (_opcSession != null)
                        {
                            ConfigureConnectedSession(_opcSession);
                        }
                    }
                }

                _ = PublishStatusSafeAsync("在线/重连恢复(OPC UA)", 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理 OPC UA 重连完成回调时发生异常");
            }
        }

        private void OnCertificateValidation(CertificateValidator sender, CertificateValidationEventArgs e)
        {
            try
            {
                if (e.Error != null && e.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
                {
                    _logger.LogWarning("接受不受信任的服务端证书: {Subject}", e.Certificate?.Subject);
                    e.Accept = true;
                    return;
                }

                if (e.Error != null)
                {
                    _logger.LogWarning(
                        "服务端证书校验未通过: {Status}, Subject={Subject}",
                        e.Error.StatusCode,
                        e.Certificate?.Subject);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理证书校验回调时发生异常");
            }
        }

        private async Task PublishStatusSafeAsync(string statusText, int statusCode)
        {
            try
            {
                await _publisher
                    .PublishDeviceStatusAsync(_device.DeviceId, statusText, statusCode)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "发布设备状态失败: {StatusText}", statusText);
            }
        }

        /// <summary>
        /// 判断后台启动重试任务是否仍在运行。
        /// </summary>
        private bool IsStartRetryTaskRunning()
        {
            return _startRetryTask != null && !_startRetryTask.IsCompleted;
        }

        /// <summary>
        /// 数据变更回调。
        ///
        /// 这里保持你的现有链路不动：
        /// OPC UA 推送 -> PointConfig(Handle) -> IDataProcessor -> IMqttPublisher
        /// </summary>
        private void OnDataChanged(MonitoredItem item, MonitoredItemNotificationEventArgs e)
        {
            try
            {
                var notification = e.NotificationValue as MonitoredItemNotification;
                if (notification == null)
                {
                    return;
                }

                var point = item.Handle as PointConfig;
                if (point == null)
                {
                    _logger.LogWarning("MonitoredItem.Handle 未找到 PointConfig，点位: {DisplayName}", item.DisplayName);
                    return;
                }

                DataValue dataValue = notification.Value;
                object realValue = dataValue.Value;
                bool isGood = StatusCode.IsGood(dataValue.StatusCode);

                var rawResult = isGood
                    ? HslCommunication.OperateResult.CreateSuccessResult(realValue)
                    : new HslCommunication.OperateResult<object>($"OPC UA 状态码异常: {dataValue.StatusCode}");

                StandardPointData processedData = _processor.Process(_device, point, rawResult);

                if (dataValue.SourceTimestamp > DateTime.MinValue)
                {
                    processedData.CollectTime = dataValue.SourceTimestamp.ToLocalTime();
                }

                // 保留你现有策略：
                // 回调线程不 await，不阻塞 OPC 底层线程。
                _ = _publisher.PublishPointDataAsync(processedData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理 OPC UA 数据变更回调时发生异常");
            }
        }
    }
}