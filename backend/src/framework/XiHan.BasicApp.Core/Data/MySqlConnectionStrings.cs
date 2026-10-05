// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using MySqlConnector;
using SqlSugar;

namespace XiHan.BasicApp.Core.Data;

/// <summary>
/// MySQL 连接串规范化：保证连接以 UTC 读写时间列
/// </summary>
public static class MySqlConnectionStrings
{
    private const string DateTimeKindKey = "DateTimeKind";

    /// <summary>
    /// 缺少 DateTimeKind 时补上 Utc，其余连接选项保持不变
    /// </summary>
    /// <param name="connectionString">MySQL 连接串</param>
    /// <returns>带 DateTimeKind=Utc 的连接串</returns>
    /// <exception cref="InvalidOperationException">连接串显式声明了 Utc 以外的 DateTimeKind</exception>
    public static string EnsureUtcDateTimeKind(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new MySqlConnectionStringBuilder(connectionString);
        if (!builder.ContainsKey(DateTimeKindKey))
        {
            builder.DateTimeKind = MySqlDateTimeKind.Utc;
            return builder.ConnectionString;
        }

        if (builder.DateTimeKind != MySqlDateTimeKind.Utc)
        {
            throw new InvalidOperationException($"MySQL 连接必须使用 DateTimeKind=Utc，当前声明为 {builder.DateTimeKind}。");
        }

        return connectionString;
    }

    /// <summary>
    /// 判断数据库类型是否为 MySQL
    /// </summary>
    /// <param name="dbType">数据库类型</param>
    /// <returns>是 MySQL 时为 true</returns>
    public static bool IsMySql(DbType dbType)
    {
        return dbType is DbType.MySql or DbType.MySqlConnector;
    }

    /// <summary>
    /// 规范化 MySQL 连接配置的主库与从库连接串，其他数据库类型不处理
    /// </summary>
    /// <param name="config">连接配置</param>
    public static void Apply(ConnectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!IsMySql(config.DbType))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            config.ConnectionString = EnsureUtcDateTimeKind(config.ConnectionString);
        }

        if (config.SlaveConnectionConfigs is null)
        {
            return;
        }

        foreach (var slave in config.SlaveConnectionConfigs)
        {
            if (!string.IsNullOrWhiteSpace(slave.ConnectionString))
            {
                slave.ConnectionString = EnsureUtcDateTimeKind(slave.ConnectionString);
            }
        }
    }
}
