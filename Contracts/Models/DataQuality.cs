using System;
using System.Collections.Generic;
using System.Text;

namespace Contracts.Models
{
    public enum DataQuality
    {
        Good,          // 数据正常
        Bad,           // 数据无效或异常
        Uncertain      // 数据质量不确定
    }
}
