using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Serilog.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Permissions;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Collector.UI.ViewModel
{
    public partial class MainViewModel : ObservableRecipient
    {

        [ObservableProperty]
        private string message = "YK_Scada";


        [ObservableProperty]
        private int index = 0;

        public List<ObservableRecipient> ViewModels { get; }


        public MainViewModel(Page1ViewModel p1)
        {
            ViewModels = new List<ObservableRecipient> { p1 };

            //开启接收订阅消息，订阅消息在OnActivated中
            IsActive = true;


            WeakReferenceMessenger.Default.Send<string, string>("来自ViewModel的消息", "main");
        }


















    }
}
