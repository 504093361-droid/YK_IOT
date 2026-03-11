using HandyControl.Controls;
using HandyControl.Interactivity;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using YK_SCADA.Tools;

namespace YK_SCADA.Controls
{
    /// <summary>
    /// Dialog.xaml 的交互逻辑
    /// </summary>
    public partial class MyDialog : UserControl
    {



        public MyDialog()
        {
            InitializeComponent();


        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {


        }


        private void Close()
        {
            // 创建Command实例
            RoutedCommand closeCmd = new RoutedCommand();
            closeCmd = ControlCommands.Close;


            closeCmd.Execute(null, this);
        }




    }
}
