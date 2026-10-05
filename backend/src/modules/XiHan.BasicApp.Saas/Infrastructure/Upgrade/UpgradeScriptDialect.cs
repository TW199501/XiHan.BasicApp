// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;

namespace XiHan.BasicApp.Saas.Infrastructure.Upgrade;

/// <summary>
/// 升级脚本方言目录
/// </summary>
public static class UpgradeScriptDialect
{
    /// <summary>
    /// PostgreSQL 方言目录名；版本目录根层的 .sql 也归入此方言
    /// </summary>
    public const string PostgreSql = "pgsql";

    /// <summary>
    /// SQL Server 方言目录名
    /// </summary>
    public const string SqlServer = "mssql";

    /// <summary>
    /// MySQL 方言目录名
    /// </summary>
    public const string MySql = "mysql";

    /// <summary>
    /// 全部已知方言目录名
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [PostgreSql, SqlServer, MySql];

    /// <summary>
    /// 取数据库类型对应的方言目录名
    /// </summary>
    /// <param name="dbType">数据库类型</param>
    /// <returns>方言目录名；没有对应方言时返回 null</returns>
    public static string? From(DbType dbType)
    {
        return dbType switch
        {
            DbType.PostgreSQL => PostgreSql,
            DbType.SqlServer => SqlServer,
            DbType.MySql or DbType.MySqlConnector => MySql,
            _ => null
        };
    }

    /// <summary>
    /// 判断目录名是否为已知方言（不区分大小写）
    /// </summary>
    /// <param name="directoryName">目录名</param>
    /// <returns>是已知方言返回 true</returns>
    public static bool IsKnown(string directoryName)
    {
        return All.Contains(directoryName, StringComparer.OrdinalIgnoreCase);
    }
}
