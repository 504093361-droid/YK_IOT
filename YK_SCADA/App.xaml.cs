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
using YK_SCADA.Tools;
using YK_SCADA.ViewModel;
using YK_SCADA.Views;


namespace YK_SCADA
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

            services.AddSqlSugar(); //注册sqlsguar




            return services.BuildServiceProvider();
        }



        async void Applicaton_Startup(object sender, StartupEventArgs e)
        {
            var mainview = Services.GetService<MainWindow>();

#if DEBUG
            mainview.Width = 920;
            mainview.Height = 550;
            mainview.WindowState = WindowState.Normal;

#elif !DEBUG

            CreateShortcutInStartup();

            mainview.WindowState = WindowState.Maximized;

            CheckApplicationMutex("YK_SCADA", "YK_SCADA");
#endif





            await ActiveHsl();


            mainview!.Show();




        }






        private async Task ActiveHsl()
        {
            //能用10年
            if (!HslCommunication.Authorization.SetAuthorizationCode("4c30051d-fb61-48c2-979b-2ff92a40faff"))
            {
                Growl.WarningGlobal("激活Hsl失败");
            }
        }

        public void CreateShortcutInStartup()
        {
            try
            {
                // 获取启动文件夹的路径
                string startupFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string shortcutPath = System.IO.Path.Combine(startupFolderPath, "YK_SCADA.lnk");


                // 设置目标路径为程序的EXE路径
                string targetPath = @INIHelper.Read("targetPath"); // 程序路径

                // 创建快捷方式
                WshShell shell = new WshShell();
                IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(shortcutPath);
                shortcut.Description = "喂料铲车程序";
                shortcut.TargetPath = targetPath;
                shortcut.Save();
            }
            catch (Exception ex)
            {
                loger.Error(ex.Message);
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
