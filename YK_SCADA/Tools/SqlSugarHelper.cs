using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using static YK_SCADA.Tools.Global;

namespace YK_SCADA.Tools
{
    public class SqlSugarHelper
    {
        private static readonly string uid = INIHelper.Read("database", "uid", App.inipath);
        private static readonly string pwd = INIHelper.Read("database", "pwd", App.inipath);
        private static string database = INIHelper.Read("database", "name", App.inipath);

        // 动态创建 SqlSugarClient 实例的方法
        private SqlSugarClient CreateSqlSugarClient()
        {
            string ip = INIHelper.Read("database", "ip_s" + Sdic["nowdb"]);


            return new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Server={ip};Uid={uid};Pwd={pwd};Database={database};Charset=utf8mb4",
                DbType = DbType.MySql,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute
            });
        }

        #region 远程端

        // 查询数据
        public List<T> Query<T>() where T : class, new()
        {
            using (var db = CreateSqlSugarClient())
            {
                return db.Queryable<T>().ToList();
            }
        }

        public async Task<List<T>> QueryAsync<T>() where T : class, new()
        {
            using (var db = CreateSqlSugarClient())
            {
                return await db.Queryable<T>().ToListAsync();
            }
        }

        /// <summary>
        /// 同步查询方法，支持筛选、排序和其他链式调用
        /// </summary>
        public List<T> Query<T>(Func<ISugarQueryable<T>, ISugarQueryable<T>> queryBuilder) where T : class, new()
        {
            using (var db = CreateSqlSugarClient())
            {
                var queryable = db.Queryable<T>();
                var query = queryBuilder?.Invoke(queryable) ?? queryable;
                return query.ToList();
            }
        }

        /// <summary>
        /// 异步查询方法，支持筛选、排序和其他链式调用
        /// </summary>
        public async Task<List<T>> QueryAsync<T>(Func<ISugarQueryable<T>, ISugarQueryable<T>> queryBuilder) where T : class, new()
        {
            using (var db = CreateSqlSugarClient())
            {
                var queryable = db.Queryable<T>();
                var query = queryBuilder?.Invoke(queryable) ?? queryable;
                return await query.ToListAsync();
            }
        }

        // 插入数据
        public void Insert<T>(T entity) where T : class, new()
        {
            using (var db = CreateSqlSugarClient())
            {
                db.Insertable(entity).ExecuteCommand();
            }
        }

        public async Task InsertAsync<T>(T entity) where T : class, new()
        {
            using (var db = CreateSqlSugarClient())
            {
                await db.Insertable(entity).ExecuteCommandAsync();
            }
        }

        // 批量插入数据
        public void InsertAll<T>(List<T> entities) where T : class, new()
        {
            if (entities == null || !entities.Any())
                throw new ArgumentException("Cannot insert an empty list.");

            using (var db = CreateSqlSugarClient())
            {
                db.Insertable(entities).ExecuteCommand();
            }
        }

        // 更新数据
        public void Update<T>(T entity) where T : class, new()
        {
            using (var db = CreateSqlSugarClient())
            {
                db.Updateable(entity).ExecuteCommand();
            }
        }

        public async Task UpdateAsync<T>(T entity) where T : class, new()
        {
            using (var db = CreateSqlSugarClient())
            {
                await db.Updateable(entity).ExecuteCommandAsync();
            }
        }

        // 更新数据中的指定字段
        public void Update<T>(Expression<Func<T, T>> columns, Expression<Func<T, bool>> whereExpression) where T : class, new()
        {
            using (var db = CreateSqlSugarClient())
            {
                db.Updateable<T>().SetColumns(columns).Where(whereExpression).ExecuteCommand();
            }
        }

        public async Task UpdateAsync<T>(Expression<Func<T, T>> columns, Expression<Func<T, bool>> whereExpression) where T : class, new()
        {
            using (var db = CreateSqlSugarClient())
            {
                await db.Updateable<T>().SetColumns(columns).Where(whereExpression).ExecuteCommandAsync();
            }
        }

        public void UpdateAll<T>(List<T> entities) where T : class, new()
        {
            if (entities == null || !entities.Any())
                throw new ArgumentException("Cannot update an empty list.");

            using (var db = CreateSqlSugarClient())
            {
                db.Updateable(entities).ExecuteCommand();
            }
        }

        public async Task UpdateAllAsync<T>(List<T> entities) where T : class, new()
        {
            if (entities == null || !entities.Any())
                throw new ArgumentException("Cannot update an empty list.");

            using (var db = CreateSqlSugarClient())
            {
                await db.Updateable(entities).ExecuteCommandAsync();
            }
        }

        // 删除数据
        public void Delete<T>(T entity) where T : class, new()
        {
            using (var db = CreateSqlSugarClient())
            {
                db.Deleteable(entity).ExecuteCommand();
            }
        }

        // 删除指定字段数据
        public void Delete<T>(Expression<Func<T, bool>> whereExpression) where T : class, new()
        {
            using (var db = CreateSqlSugarClient())
            {
                db.Deleteable<T>().Where(whereExpression).ExecuteCommand();
            }
        }
    }

    #endregion 远程端
}