// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.BasicApp.Core.Data;

namespace XiHan.BasicApp.Saas.Tests.TestDatabases;

/// <summary>
/// 真实数据库集成测试的连接入口
/// </summary>
/// <remarks>
/// 连接串从环境变量读取，ConfigId 为 IntegrationTest_ 加数据库种类，MySQL 连接配置经 MySqlConnectionStrings 规范化。
/// </remarks>
public static class IntegrationDatabase
{
    /// <summary>
    /// PostgreSQL 连接串环境变量
    /// </summary>
    public const string PostgresVariable = "XIHAN_TEST_POSTGRES";

    /// <summary>
    /// SQL Server 连接串环境变量
    /// </summary>
    public const string SqlServerVariable = "XIHAN_TEST_SQLSERVER";

    /// <summary>
    /// MySQL 连接串环境变量
    /// </summary>
    public const string MySqlVariable = "XIHAN_TEST_MYSQL";

    /// <summary>
    /// 读取连接串，未设置或只含空白时返回 null
    /// </summary>
    /// <param name="variable">环境变量名</param>
    /// <returns>连接串</returns>
    public static string? GetConnectionString(string variable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);

        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// 建立连接配置
    /// </summary>
    /// <param name="dbType">数据库种类</param>
    /// <param name="connectionString">连接串</param>
    /// <returns>连接配置</returns>
    public static ConnectionConfig CreateConnectionConfig(DbType dbType, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var config = new ConnectionConfig
        {
            ConfigId = $"IntegrationTest_{dbType}",
            ConnectionString = connectionString,
            DbType = dbType,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute
        };
        MySqlConnectionStrings.Apply(config);
        return config;
    }

    /// <summary>
    /// 建立客户端
    /// </summary>
    /// <param name="dbType">数据库种类</param>
    /// <param name="connectionString">连接串</param>
    /// <returns>客户端</returns>
    public static SqlSugarClient CreateClient(DbType dbType, string connectionString)
    {
        return new SqlSugarClient(CreateConnectionConfig(dbType, connectionString));
    }

    /// <summary>
    /// 建立线程安全的客户端，供并发用例使用
    /// </summary>
    /// <param name="dbType">数据库种类</param>
    /// <param name="connectionString">连接串</param>
    /// <returns>客户端</returns>
    public static SqlSugarScope CreateScope(DbType dbType, string connectionString)
    {
        return new SqlSugarScope(CreateConnectionConfig(dbType, connectionString));
    }

    /// <summary>
    /// 沿内部异常逐层查找指定类型的异常
    /// </summary>
    /// <typeparam name="TException">异常类型</typeparam>
    /// <param name="exception">最外层异常</param>
    /// <returns>找到的异常，没有时为 null</returns>
    public static TException? FindException<TException>(Exception exception)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException match)
            {
                return match;
            }
        }

        return null;
    }
}
