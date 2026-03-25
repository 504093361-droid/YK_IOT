
using Collector.UI.Service;
using Collector.UI.ViewModel;
using Collector.UI.Views;
using Contracts.Interface;
using HandyControl.Controls;
using IWshRuntimeLibrary;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace Collector.UI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private ILogger loger;
        public static string inipath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "Config.ini");




        public new static App Current => (App)Application.Current;

        public IServiceProvider Services { get; }

        public App()
        {
            Services = ConfigureSurivces();
            loger = Services.GetService<ILogger>();

        }



        private static IServiceProvider ConfigureSurivces()
        {
            var services = new ServiceCollection();


            #region   日志、INI文件

            // Log  按照日期分目录
            services.AddSingleton<ILogger>(_ =>
            {
                return new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File("./logs/log_.txt", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 180)  //一天一个，保存半年
                .CreateLogger();
            });


            #endregion


            services.AddTransient<MainWindow>();
            services.AddSingleton<MainViewModel>(); //单例模式 singleton

            services.AddScoped<Page1ViewModel>();

            // 注册基础服务
            services.AddSingleton<IMqttService, MqttService>();




            return services.BuildServiceProvider();
        }



        async void Applicaton_Startup(object sender, StartupEventArgs e)
        {
            var mainview = Services.GetService<MainWindow>();

#if DEBUG
      
            mainview.WindowState = WindowState.Normal;

#elif !DEBUG

            CreateShortcutInStartup();

            mainview.WindowState = WindowState.Maximized;

            CheckApplicationMutex("Collector.UI", "Collector.UI");
#endif





            await ActiveHsl();


            mainview!.Show();




        }






        private async Task ActiveHsl()
        {
            //能用10年
            if (!HslCommunication.Authorization.SetAuthorizationCode("4b86f3fc-f650-3b08-5924-b0f8278d6ed2"))
            {
                Growl.WarningGlobal("激活Hsl失败");
            }
        }






        #region 捕获全局异常


        //未捕获的UI异常
        void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            //打印log
            loger.Error(e.Exception.ToString());

            Growl.ErrorGlobal("捕获到异常，已写入日志");

            //处理完后，需要将 Handler = true 表示已处理过此异常
            e.Handled = true;
        }


        // 应用程序中未处理的异常捕获
        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exception = (Exception)e.ExceptionObject;

            Growl.ErrorGlobal("捕获到异常，已写入日志");
            // 处理异常，例如记录日志、显示用户通知等
            loger.Error(exception.ToString());

            // 退出应用程序
            Environment.Exit(0); // 或者 Application.Current.Shutdown()
        }

        #endregion

        #region    检查当前进程
        /// <summary>
        /// 进程
        /// </summary>
        private Mutex mutex;
        /// 检查应用程序是否在进程中已经存在了
        private void CheckApplicationMutex(string APP_NameSpace, string APP_Title)
        {
            bool mutexResult;

            // 第二个参数为 你的工程命名空间名。
            // out 给 ret 为 false 时，表示已有相同实例运行。
            mutex = new Mutex(true, APP_NameSpace, out mutexResult);

            if (!mutexResult)
            {
                // 找到已经在运行的实例句柄(给出你的窗体标题名 “Deamon Club”)
                IntPtr hWndPtr = FindWindow(null, APP_Title);

                // 还原窗口
                ShowWindow(hWndPtr, SW_RESTORE);

                // 激活窗口
                SetForegroundWindow(hWndPtr);

                // 退出当前实例程序
                Environment.Exit(0);
            }
        }

        #endregion

        #region Windows API

        // ShowWindow 参数  
        private const int SW_RESTORE = 9;

        /// <summary>
        /// 在桌面窗口列表中寻找与指定条件相符的第一个窗口。
        /// </summary>
        /// <param name="lpClassName">指向指定窗口的类名。如果 lpClassName 是 NULL，所有类名匹配。</param>
        /// <param name="lpWindowName">指向指定窗口名称(窗口的标题）。如果 lpWindowName 是 NULL，所有windows命名匹配。</param>
        /// <returns>返回指定窗口句柄</returns>
        [DllImport("USER32.DLL", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        /// <summary>
        /// 将窗口还原,可从最小化还原
        /// </summary>
        /// <param name="hWnd"></param>
        /// <param name="nCmdShow"></param>
        /// <returns></returns>
        [DllImport("USER32.DLL")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>
        /// 激活指定窗口
        /// </summary>
        /// <param name="hWnd">指定窗口句柄</param>
        /// <returns></returns>
        [DllImport("USER32.DLL")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        #endregion




    }
}
