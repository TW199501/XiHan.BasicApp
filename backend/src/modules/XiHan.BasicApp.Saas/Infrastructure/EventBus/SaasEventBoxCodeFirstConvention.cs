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
/// 在 SQL Server 使用 <c>nvarchar</c> 与由数据库默认排序规则推导的排序规则（见 <see cref="GetSqlServerDedupKeyCollationCandidates"/>
/// 与 <see cref="SelectSqlServerDedupKeyCollation"/>），首次映射该列时按连接解析一次。比较与唯一索引都区分大小写，其他数据库沿用默认列定义。
/// 只影响建表，不修改已存在的表；SQL Server 既有库的修复脚本见 <c>backend/scripts/upgrade/mssql/sys-event-inbox-dedup-key-collation.sql</c>。
/// </remarks>
public static class SaasEventBoxCodeFirstConvention
{
    /// <summary>
    /// MySQL 去重键排序规则
    /// </summary>
    public const string MySqlDedupKeyCollation = "utf8mb4_bin";

    /// <summary>
    /// 在连接配置上挂接去重键列定义，已有的实体服务先执行；SQL Server 的排序规则按该连接的数据库解析
    /// </summary>
    /// <param name="config">连接配置</param>
    public static void Apply(ConnectionConfig config)
    {
        Apply(config, ResolveSqlServerDedupKeyCollation);
    }

    /// <summary>
    /// 在连接配置上挂接去重键列定义，已有的实体服务先执行
    /// </summary>
    /// <param name="config">连接配置</param>
    /// <param name="resolveSqlServerDedupKeyCollation">解析 SQL Server 去重键排序规则；解析失败时下一次映射重新解析</param>
    public static void Apply(ConnectionConfig config, Func<ConnectionConfig, string> resolveSqlServerDedupKeyCollation)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(resolveSqlServerDedupKeyCollation);

        Func<int, string> getColumnType;
        switch (config.DbType)
        {
            case DbType.MySql or DbType.MySqlConnector:
                getColumnType = length => $"varchar({length}) COLLATE {MySqlDedupKeyCollation}";
                break;
            case DbType.SqlServer:
                var collation = new Lazy<string>(
                    () => resolveSqlServerDedupKeyCollation(config),
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
    /// 获取由数据库默认排序规则推导的去重键候选排序规则，按优先顺序排列
    /// </summary>
    /// <remarks>
    /// 按下划线分段，<c>CI</c> 换成 <c>CS</c>、<c>AI</c> 换成 <c>AS</c>，去掉原有的 <c>KS</c>、<c>WS</c>，得到 <c>&lt;base&gt;_CS_AS</c>；
    /// 再在 <c>CS_AS</c> 之后插入 <c>KS_WS</c>，得到优先的 <c>&lt;base&gt;_CS_AS_KS_WS</c>，其余分段保持原位。二进制排序规则原样作为唯一候选。
    /// </remarks>
    /// <param name="databaseCollation">数据库默认排序规则</param>
    /// <returns>候选排序规则，先 <c>CS_AS_KS_WS</c> 后 <c>CS_AS</c></returns>
    /// <exception cref="ArgumentException">名称中既没有 <c>CI</c>/<c>CS</c> 分段，也不是二进制排序规则</exception>
    public static IReadOnlyList<string> GetSqlServerDedupKeyCollationCandidates(string databaseCollation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseCollation);

        var parts = databaseCollation.Split('_');
        if (parts.Any(part => part is "BIN" or "BIN2"))
        {
            return [databaseCollation];
        }

        if (!parts.Any(part => part is "CI" or "CS"))
        {
            throw new ArgumentException($"无法识别排序规则的大小写设置：{databaseCollation}", nameof(databaseCollation));
        }

        var sensitive = parts
            .Where(part => part is not ("KS" or "WS"))
            .Select(part => part switch
            {
                "CI" => "CS",
                "AI" => "AS",
                _ => part
            })
            .ToList();
        var fallback = string.Join('_', sensitive);
        var preferred = string.Join('_', sensitive.SelectMany(part => part == "AS" ? new[] { "AS", "KS", "WS" } : [part]));
        return [preferred, fallback];
    }

    /// <summary>
    /// 从候选排序规则中选用第一个可用的
    /// </summary>
    /// <param name="databaseCollation">数据库默认排序规则</param>
    /// <param name="availableCollations">数据库支持的排序规则</param>
    /// <returns>去重键排序规则</returns>
    /// <exception cref="InvalidOperationException">没有可用的候选</exception>
    public static string SelectSqlServerDedupKeyCollation(string databaseCollation, IEnumerable<string> availableCollations)
    {
        ArgumentNullException.ThrowIfNull(availableCollations);

        var available = availableCollations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = GetSqlServerDedupKeyCollationCandidates(databaseCollation);
        return candidates.FirstOrDefault(available.Contains)
            ?? throw new InvalidOperationException($"数据库不支持由默认排序规则 {databaseCollation} 推导的排序规则：{string.Join("、", candidates)}");
    }

    private static string ResolveSqlServerDedupKeyCollation(ConnectionConfig config)
    {
        using var connection = new SqlConnection(config.ConnectionString);
        connection.Open();

        using var defaultCommand = new SqlCommand("SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))", connection);
        var databaseCollation = defaultCommand.ExecuteScalar() as string
            ?? throw new InvalidOperationException("未能读取 SQL Server 数据库的默认排序规则。");

        var candidates = GetSqlServerDedupKeyCollationCandidates(databaseCollation);
        using var availableCommand = new SqlCommand("SELECT name FROM sys.fn_helpcollations() WHERE name IN (@preferred, @fallback)", connection);
        availableCommand.Parameters.AddWithValue("@preferred", candidates[0]);
        availableCommand.Parameters.AddWithValue("@fallback", candidates[^1]);
        var available = new List<string>();
        using (var reader = availableCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                available.Add(reader.GetString(0));
            }
        }

        return SelectSqlServerDedupKeyCollation(databaseCollation, available);
    }
}
