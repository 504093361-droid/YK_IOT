using CommunityToolkit.Mvvm.ComponentModel;
using HandyControl.Controls;
using Serilog;
using System.Threading.Tasks;

namespace YK_SCADA.ViewModel
{
    public partial class Page1ViewModel : ObservableRecipient
    {
        #region 字段定义


        [ObservableProperty]
        private string header = "Test";


        #endregion 字段定义

        public Page1ViewModel(ILogger loger)
        {



        }


    }
}