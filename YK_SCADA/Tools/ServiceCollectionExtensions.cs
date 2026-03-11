using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace YK_SCADA.Tools
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddSqlSugar(this IServiceCollection services)
        {
            services.AddTransient<SqlSugarHelper>();
            return services;
        }
    }
}