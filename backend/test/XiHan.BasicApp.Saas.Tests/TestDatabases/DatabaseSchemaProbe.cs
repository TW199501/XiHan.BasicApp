// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;
using SqlSugar;
using DataRow = System.Data.DataRow;

namespace XiHan.BasicApp.Saas.Tests.TestDatabases;

/// <summary>
/// 数据库中的列定义
/// </summary>
/// <param name="Name">列名（小写）</param>
/// <param name="DataType">数据类型（小写）</param>
/// <param name="MaxLength">字符或二进制最大长度；SQL Server 的 max 为 -1；没有长度时为 null</param>
/// <param name="DateTimePrecision">时间类型的小数秒位数；非时间类型为 null</param>
/// <param name="Collation">排序规则；使用数据库默认值或非字符类型时可能为 null</param>
public sealed record DatabaseColumn(string Name, string DataType, long? MaxLength, int? DateTimePrecision, string? Collation);

/// <summary>
/// 数据库中的索引定义
/// </summary>
/// <param name="Name">索引名（小写）</param>
/// <param name="IsUnique">是否唯一</param>
/// <param name="Columns">按键顺序排列的列名（小写）</param>
public sealed record DatabaseIndex(string Name, bool IsUnique, IReadOnlyList<string> Columns);

/// <summary>
/// 按数据库种类查询表的列与索引定义
/// </summary>
/// <remarks>
/// 支持 PostgreSQL、SQL Server 与 MySQL；表名按小写匹配，返回的列名与索引名统一转为小写。
/// </remarks>
public static class DatabaseSchemaProbe
{
    /// <summary>
    /// 查询表的列定义
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="tableName">表名</param>
    /// <returns>以小写列名为键的列定义</returns>
    /// <exception cref="InvalidOperationException">表不存在或没有列</exception>
    public static async Task<IReadOnlyDictionary<string, DatabaseColumn>> GetColumnsAsync(ISqlSugarClient client, string tableName)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var table = await client.Ado.GetDataTableAsync(GetColumnsSql(client.CurrentConnectionConfig.DbType), new { tableName });

        var columns = new Dictionary<string, DatabaseColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow row in table.Rows)
        {
            var name = ReadString(row, "column_name")!.ToLowerInvariant();
            var precision = ReadInt64(row, "datetime_precision");
            columns[name] = new DatabaseColumn(
                name,
                ReadString(row, "data_type")!.ToLowerInvariant(),
                ReadInt64(row, "max_length"),
                precision is null ? null : (int)precision.Value,
                ReadString(row, "collation_name"));
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"表 {tableName} 不存在或没有列。");
        }

        return columns;
    }

    /// <summary>
    /// 查询表的索引定义，包含主键索引
    /// </summary>
    /// <param name="client">客户端</param>
    /// <param name="tableName">表名</param>
    /// <returns>以小写索引名为键的索引定义</returns>
    public static async Task<IReadOnlyDictionary<string, DatabaseIndex>> GetIndexesAsync(ISqlSugarClient client, string tableName)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var table = await client.Ado.GetDataTableAsync(GetIndexesSql(client.CurrentConnectionConfig.DbType), new { tableName });

        var uniqueness = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow row in table.Rows)
        {
            var name = ReadString(row, "index_name")!.ToLowerInvariant();
            if (!columns.TryGetValue(name, out var list))
            {
                list = [];
                columns[name] = list;
                uniqueness[name] = Convert.ToBoolean(row["is_unique"], CultureInfo.InvariantCulture);
            }

            list.Add(ReadString(row, "column_name")!.ToLowerInvariant());
        }

        return columns.ToDictionary(
            pair => pair.Key,
            pair => new DatabaseIndex(pair.Key, uniqueness[pair.Key], pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string GetColumnsSql(DbType dbType)
    {
        return dbType switch
        {
            DbType.PostgreSQL =>
                "SELECT column_name AS column_name, data_type AS data_type, character_maximum_length AS max_length, " +
                "datetime_precision AS datetime_precision, collation_name AS collation_name " +
                "FROM information_schema.columns " +
                "WHERE table_schema = current_schema() AND lower(table_name) = lower(@tableName)",
            DbType.SqlServer =>
                "SELECT COLUMN_NAME AS column_name, DATA_TYPE AS data_type, CHARACTER_MAXIMUM_LENGTH AS max_length, " +
                "DATETIME_PRECISION AS datetime_precision, COLLATION_NAME AS collation_name " +
                "FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA = SCHEMA_NAME() AND LOWER(TABLE_NAME) = LOWER(@tableName)",
            DbType.MySql or DbType.MySqlConnector =>
                "SELECT COLUMN_NAME AS column_name, DATA_TYPE AS data_type, CHARACTER_MAXIMUM_LENGTH AS max_length, " +
                "DATETIME_PRECISION AS datetime_precision, COLLATION_NAME AS collation_name " +
                "FROM information_schema.columns " +
                "WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER(@tableName)",
            _ => throw new NotSupportedException($"不支持的数据库种类：{dbType}")
        };
    }

    private static string GetIndexesSql(DbType dbType)
    {
        return dbType switch
        {
            DbType.PostgreSQL =>
                "SELECT i.relname AS index_name, ix.indisunique AS is_unique, a.attname AS column_name, " +
                "array_position(CAST(ix.indkey AS smallint[]), a.attnum) AS ordinal " +
                "FROM pg_index ix " +
                "JOIN pg_class t ON t.oid = ix.indrelid " +
                "JOIN pg_class i ON i.oid = ix.indexrelid " +
                "JOIN pg_namespace n ON n.oid = t.relnamespace " +
                "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey) " +
                "WHERE n.nspname = current_schema() AND lower(t.relname) = lower(@tableName) " +
                "ORDER BY index_name, ordinal",
            DbType.SqlServer =>
                "SELECT i.name AS index_name, CAST(i.is_unique AS int) AS is_unique, c.name AS column_name, ic.key_ordinal AS ordinal " +
                "FROM sys.indexes i " +
                "JOIN sys.tables t ON t.object_id = i.object_id " +
                "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
                "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                "WHERE t.schema_id = SCHEMA_ID() AND LOWER(t.name) = LOWER(@tableName) " +
                "AND i.name IS NOT NULL AND ic.is_included_column = 0 " +
                "ORDER BY i.name, ic.key_ordinal",
            DbType.MySql or DbType.MySqlConnector =>
                "SELECT INDEX_NAME AS index_name, CASE WHEN NON_UNIQUE = 0 THEN 1 ELSE 0 END AS is_unique, " +
                "COLUMN_NAME AS column_name, SEQ_IN_INDEX AS ordinal " +
                "FROM information_schema.statistics " +
                "WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER(@tableName) " +
                "ORDER BY INDEX_NAME, SEQ_IN_INDEX",
            _ => throw new NotSupportedException($"不支持的数据库种类：{dbType}")
        };
    }

    private static string? ReadString(DataRow row, string column)
    {
        return row[column] switch
        {
            DBNull => null,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            var value => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static long? ReadInt64(DataRow row, string column)
    {
        return row[column] is DBNull ? null : Convert.ToInt64(row[column], CultureInfo.InvariantCulture);
    }
}
