// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Data.SqlClient;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;

namespace XiHan.BasicApp.Saas.Infrastructure.EventBus;

/// <summary>
/// 收发件箱的 CodeFirst 列定义惯例
/// </summary>
/// <remarks>
/// 收件箱去重键在 MySQL 使用 <c>varchar</c> 与 <see cref="MySqlDedupKeyCollation"/>；
/// 在 SQL Server 使用 <c>nvarchar</c> 与数据库默认排序规则对应的区分大小写版本（见 <see cref="ToCaseSensitiveCollation"/>），
/// 数据库默认排序规则在首次映射该列时读取。比较与唯一索引都区分大小写，其他数据库沿用默认列定义。
/// 只影响建表，不修改已存在的表；SQL Server 既有库的修复脚本见 <c>backend/scripts/upgrade/mssql/sys-event-inbox-dedup-key-collation.sql</c>。
/// </remarks>
public static class SaasEventBoxCodeFirstConvention
{
    /// <summary>
    /// MySQL 去重键排序规则
    /// </summary>
    public const string MySqlDedupKeyCollation = "utf8mb4_bin";

    /// <summary>
    /// 在连接配置上挂接去重键列定义，已有的实体服务先执行；SQL Server 的默认排序规则从该连接的数据库读取
    /// </summary>
    /// <param name="config">连接配置</param>
    public static void Apply(ConnectionConfig config)
    {
        Apply(config, ReadSqlServerDatabaseCollation);
    }

    /// <summary>
    /// 在连接配置上挂接去重键列定义，已有的实体服务先执行
    /// </summary>
    /// <param name="config">连接配置</param>
    /// <param name="readSqlServerDatabaseCollation">读取 SQL Server 数据库默认排序规则；读取失败时下一次映射重新读取</param>
    public static void Apply(ConnectionConfig config, Func<ConnectionConfig, string> readSqlServerDatabaseCollation)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(readSqlServerDatabaseCollation);

        Func<int, string> getColumnType;
        switch (config.DbType)
        {
            case DbType.MySql or DbType.MySqlConnector:
                getColumnType = length => $"varchar({length}) COLLATE {MySqlDedupKeyCollation}";
                break;
            case DbType.SqlServer:
                var collation = new Lazy<string>(
                    () => ToCaseSensitiveCollation(readSqlServerDatabaseCollation(config)),
                    LazyThreadSafetyMode.PublicationOnly);
                getColumnType = length => $"nvarchar({length}) COLLATE {collation.Value}";
                break;
            default:
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
                column.DataType = getColumnType(column.Length);
                column.Length = 0;
            }
        };
    }

    /// <summary>
    /// 获取排序规则对应的区分大小写、区分重音版本
    /// </summary>
    /// <remarks>
    /// 按下划线分段，<c>CI</c> 换成 <c>CS</c>、<c>AI</c> 换成 <c>AS</c>，其余分段不变；已区分大小写或二进制的排序规则原样返回。
    /// </remarks>
    /// <param name="collation">排序规则名称</param>
    /// <returns>区分大小写、区分重音的排序规则名称</returns>
    /// <exception cref="ArgumentException">名称中既没有 <c>CI</c>/<c>CS</c> 分段，也不是二进制排序规则</exception>
    public static string ToCaseSensitiveCollation(string collation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collation);

        var parts = collation.Split('_');
        if (!parts.Any(part => part is "CI" or "CS" or "BIN" or "BIN2"))
        {
            throw new ArgumentException($"无法识别排序规则的大小写设置：{collation}", nameof(collation));
        }

        return string.Join('_', parts.Select(part => part switch
        {
            "CI" => "CS",
            "AI" => "AS",
            _ => part
        }));
    }

    private static string ReadSqlServerDatabaseCollation(ConnectionConfig config)
    {
        using var connection = new SqlConnection(config.ConnectionString);
        connection.Open();
        using var command = new SqlCommand("SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))", connection);
        return command.ExecuteScalar() as string
            ?? throw new InvalidOperationException("未能读取 SQL Server 数据库的默认排序规则。");
    }
}
