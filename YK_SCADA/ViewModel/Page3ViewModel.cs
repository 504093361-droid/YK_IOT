using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace YK_SCADA.ViewModel
{
    public partial class Page3ViewModel : ObservableRecipient
    {
        public string Header { get; set; } = "球磨机维护";
    }
}
