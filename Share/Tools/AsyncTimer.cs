using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Share.Tools
{
    public class AsyncTimer
    {
        private Timer _timer;
        private Func<Task> _callback;
        private int _period;
        private CancellationTokenSource _cancellationTokenSource;

        /// <summary>
        /// 初始化AsyncTimer类的新实例。
        /// </summary>
        /// <param name="callback">要执行的异步任务。</param>
        /// <param name="period">任务执行的时间间隔（以毫秒为单位）。</param>
        public AsyncTimer(Func<Task> callback, int period)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            _period = period;
            _cancellationTokenSource = new CancellationTokenSource();
        }

        /// <summary>
        /// 启动定时器，开始异步执行任务。
        /// </summary>
        public void Start()
        {
            _timer = new Timer(async _ => await ExecuteAsync(), null, 0, _period);
        }

        /// <summary>
        /// 停止定时器，取消正在进行的任务。
        /// </summary>
        public void Stop()
        {
            _timer?.Change(Timeout.Infinite, 0);
            _cancellationTokenSource.Cancel();
        }

        /// <summary>
        /// 异步执行回调任务。
        /// </summary>
        private async Task ExecuteAsync()
        {
            if (_cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await _callback();
            }
            catch (Exception ex)
            {
                // 在这里可以处理异常或记录日志
                Console.WriteLine($"Error occurred: {ex.Message}");
            }
        }

        /// <summary>
        /// 释放定时器资源。
        /// </summary>
        public void Dispose()
        {
            _timer?.Dispose();
            _cancellationTokenSource?.Dispose();
        }
    }
}
