// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;

namespace XiHan.BasicApp.Saas.Infrastructure.EventBus;

/// <summary>
/// 收发件箱的 CodeFirst 列定义惯例
/// </summary>
/// <remarks>
/// 收件箱去重键在 MySQL 使用 <see cref="MySqlDedupKeyCollation"/>、在 SQL Server 使用 <see cref="SqlServerDedupKeyCollation"/>，
/// 比较与唯一索引都区分大小写；其他数据库沿用默认列定义。只影响建表，不修改已存在的表。
/// </remarks>
public static class SaasEventBoxCodeFirstConvention
{
    /// <summary>
    /// MySQL 去重键排序规则
    /// </summary>
    public const string MySqlDedupKeyCollation = "utf8mb4_0900_bin";

    /// <summary>
    /// SQL Server 去重键排序规则
    /// </summary>
    public const string SqlServerDedupKeyCollation = "Latin1_General_100_BIN2";

    /// <summary>
    /// 在连接配置上挂接去重键列定义，已有的实体服务先执行
    /// </summary>
    /// <param name="config">连接配置</param>
    public static void Apply(ConnectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var collation = GetDedupKeyCollation(config.DbType);
        if (collation is null)
        {
            return;
        }

        config.ConfigureExternalServices ??= new ConfigureExternalServices();
        var previous = config.ConfigureExternalServices.EntityService;
        config.ConfigureExternalServices.EntityService = (property, column) =>
        {
            previous?.Invoke(property, column);

            if (property.DeclaringType == typeof(SysEventInbox) &&
                property.Name == nameof(SysEventInbox.DedupKey) &&
                column.Length > 0)
            {
                column.DataType = $"varchar({column.Length}) COLLATE {collation}";
                column.Length = 0;
            }
        };
    }

    /// <summary>
    /// 获取去重键在指定数据库上的排序规则
    /// </summary>
    /// <param name="dbType">数据库种类</param>
    /// <returns>排序规则；沿用默认定义时为 null</returns>
    public static string? GetDedupKeyCollation(DbType dbType)
    {
        return dbType switch
        {
            DbType.MySql or DbType.MySqlConnector => MySqlDedupKeyCollation,
            DbType.SqlServer => SqlServerDedupKeyCollation,
            _ => null
        };
    }
}
