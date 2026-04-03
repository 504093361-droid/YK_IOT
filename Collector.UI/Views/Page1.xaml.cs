using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Collector.UI.Views
{
    /// <summary>
    /// Page1.xaml 的交互逻辑
    /// </summary>
    public partial class Page1
    {
        public Page1()
        {
            InitializeComponent();

        }

        private void DataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid dataGrid)
                return;

            if (e.OriginalSource is not DependencyObject source)
                return;

            if (FindParent<DataGridRow>(source) != null ||
                FindParent<DataGridCell>(source) != null)
            {
                return;
            }

            dataGrid.UnselectAll();
            dataGrid.SelectedItem = null;
            Keyboard.ClearFocus();
        }

        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T target)
                    return target;

                child = VisualTreeHelper.GetParent(child);
            }

            return null;
        }







    }
}
