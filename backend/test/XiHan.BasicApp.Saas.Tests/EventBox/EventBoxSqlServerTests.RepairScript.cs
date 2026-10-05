// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;

namespace XiHan.BasicApp.Saas.Tests.EventBox;

/// <summary>
/// SQL Server 收件箱去重键排序规则修复脚本测试
/// </summary>
public sealed partial class EventBoxSqlServerTests
{
    private const string InboxTable = "Sys_Event_Inbox";

    /// <summary>
    /// 建表时去重键使用 nvarchar；SQL_ 默认排序规则没有 CS_AS_KS_WS 版本，退回 CS_AS
    /// </summary>
    [Fact]
    public async Task InitTables_DedupKeyCollationIsDerivedFromDatabaseDefault()
    {
        await using var connection = await OpenConnectionAsync(RequireConnectionString());

        var databaseDefault = await ReadDatabaseCollationAsync(connection);

        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", databaseDefault);
        Assert.False(await CollationExistsAsync(connection, "SQL_Latin1_General_CP1_CS_AS_KS_WS"));
        Assert.Equal(new DedupKeyColumn("nvarchar", 512, false, "SQL_Latin1_General_CP1_CS_AS"), await ReadDedupKeyColumnAsync(connection));
    }

    /// <summary>
    /// 修复脚本把旧表的去重键改为 nvarchar 与区分大小写的排序规则，长度、可空性、索引与数据不变，再次执行不做任何改动
    /// </summary>
    [Fact]
    public async Task DedupKeyRepairScript_ConvertsLegacyColumnAndIsIdempotent()
    {
        var connectionString = RequireConnectionString();
        using var legacy = CreateLegacyClient(connectionString);
        legacy.DbMaintenance.DropTable(InboxTable);
        legacy.CodeFirst.InitTables<SysEventInbox>();
        await legacy.Insertable(new List<SysEventInbox>
        {
            NewLegacyRow("Msg-A"),
            NewLegacyRow("Msg-B"),
            NewLegacyRow("msg-é"),
            NewLegacyRow("消息-1")
        }).ExecuteCommandAsync();

        await using var connection = await OpenConnectionAsync(connectionString);
        var columnBefore = await ReadDedupKeyColumnAsync(connection);
        var indexesBefore = await ReadIndexesAsync(connection);
        var rowsBefore = await ReadRowsAsync(connection);
        Assert.Equal(new DedupKeyColumn("varchar", 256, false, await ReadDatabaseCollationAsync(connection)), columnBefore);
        Assert.Contains($"UX_{InboxTable}_DeKe|unique|NONCLUSTERED|Dedup_Key", indexesBefore);
        Assert.Equal(4, rowsBefore.Count);
        Assert.Contains(rowsBefore, row => row.Contains("|??-1|", StringComparison.Ordinal));
        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() => legacy.Insertable(NewLegacyRow("msg-a")).ExecuteCommandAsync());
        Assert.True(IsDuplicateKeyViolation(duplicate));

        await ExecuteRepairScriptAsync(connection);

        var columnAfter = await ReadDedupKeyColumnAsync(connection);
        Assert.Equal(new DedupKeyColumn("nvarchar", 512, false, "SQL_Latin1_General_CP1_CS_AS"), columnAfter);
        Assert.Equal(indexesBefore, await ReadIndexesAsync(connection));
        Assert.Equal(rowsBefore, await ReadRowsAsync(connection));
        var modifiedAfterFirstRun = await ReadTableModifyDateAsync(connection);

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await ExecuteRepairScriptAsync(connection);

        Assert.Equal(columnAfter, await ReadDedupKeyColumnAsync(connection));
        Assert.Equal(indexesBefore, await ReadIndexesAsync(connection));
        Assert.Equal(rowsBefore, await ReadRowsAsync(connection));
        Assert.Equal(modifiedAfterFirstRun, await ReadTableModifyDateAsync(connection));

        await legacy.Insertable(NewLegacyRow("msg-a")).ExecuteCommandAsync();
        Assert.Equal(5, (await ReadRowsAsync(connection)).Count);
    }

    /// <summary>
    /// 去重键已是目标排序规则时，修复脚本不做任何改动
    /// </summary>
    [Fact]
    public async Task DedupKeyRepairScript_LeavesCaseSensitiveColumnUnchanged()
    {
        await using var connection = await OpenConnectionAsync(RequireConnectionString());
        var column = await ReadDedupKeyColumnAsync(connection);
        var indexes = await ReadIndexesAsync(connection);
        var modified = await ReadTableModifyDateAsync(connection);
        Assert.Equal(new DedupKeyColumn("nvarchar", 512, false, "SQL_Latin1_General_CP1_CS_AS"), column);

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await ExecuteRepairScriptAsync(connection);

        Assert.Equal(column, await ReadDedupKeyColumnAsync(connection));
        Assert.Equal(indexes, await ReadIndexesAsync(connection));
        Assert.Equal(modified, await ReadTableModifyDateAsync(connection));
    }

    /// <summary>
    /// 修复脚本重建索引时保留筛选条件、IGNORE_DUP_KEY、填充因子、压缩与文件组
    /// </summary>
    [Fact]
    public async Task DedupKeyRepairScript_PreservesIndexOptions()
    {
        var connectionString = RequireConnectionString();
        using var legacy = CreateLegacyClient(connectionString);
        legacy.DbMaintenance.DropTable(InboxTable);
        legacy.CodeFirst.InitTables<SysEventInbox>();
        await using var connection = await OpenConnectionAsync(connectionString);
        await using (var command = new SqlCommand($"""
            DROP INDEX [UX_{InboxTable}_DeKe] ON [{InboxTable}];
            CREATE UNIQUE NONCLUSTERED INDEX [UX_{InboxTable}_DeKe] ON [{InboxTable}] ([Dedup_Key] ASC)
                WITH (IGNORE_DUP_KEY = ON, FILLFACTOR = 80, PAD_INDEX = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY];
            CREATE NONCLUSTERED INDEX [IX_{InboxTable}_DeKe_Pending] ON [{InboxTable}] ([Dedup_Key] DESC)
                INCLUDE ([Status]) WHERE [Status] = 0;
            """, connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var indexesBefore = await ReadIndexesAsync(connection);
        var optionsBefore = await ReadIndexOptionsAsync(connection);
        Assert.Contains($"UX_{InboxTable}_DeKe|1|80|1|PAGE|PRIMARY|", optionsBefore);
        Assert.Contains($"IX_{InboxTable}_DeKe_Pending|0|0|0|NONE|PRIMARY|([Status]=(0))", optionsBefore);

        await ExecuteRepairScriptAsync(connection);

        Assert.Equal("nvarchar", (await ReadDedupKeyColumnAsync(connection)).TypeName);
        Assert.Equal(indexesBefore, await ReadIndexesAsync(connection));
        Assert.Equal(optionsBefore, await ReadIndexOptionsAsync(connection));
    }

    /// <summary>
    /// 收件箱表不存在时，修复脚本不报错也不建表
    /// </summary>
    [Fact]
    public async Task DedupKeyRepairScript_WithoutInboxTable_DoesNothing()
    {
        var connectionString = RequireConnectionString();
        using var legacy = CreateLegacyClient(connectionString);
        legacy.DbMaintenance.DropTable(InboxTable);
        await using var connection = await OpenConnectionAsync(connectionString);

        await ExecuteRepairScriptAsync(connection);

        Assert.False(legacy.DbMaintenance.IsAnyTable(InboxTable, false));
    }

    private static SqlSugarClient CreateLegacyClient(string connectionString)
    {
        return new SqlSugarClient(new ConnectionConfig
        {
            ConfigId = $"legacy-{Guid.NewGuid():N}",
            ConnectionString = connectionString,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute
        });
    }

    private static SysEventInbox NewLegacyRow(string messageId)
    {
        return new SysEventInbox(Guid.NewGuid())
        {
            MessageId = messageId,
            DedupKey = messageId,
            EventName = "order.created",
            EventData = [1, 2, 3],
            CreatedTime = new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero),
            Status = SysEventInbox.StatusPending
        };
    }

    private static async Task<SqlConnection> OpenConnectionAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteRepairScriptAsync(SqlConnection connection)
    {
        var script = await File.ReadAllTextAsync(ResolveRepairScriptPath());
        await using var command = new SqlCommand(script, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadDatabaseCollationAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> CollationExistsAsync(SqlConnection connection, string collation)
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.fn_helpcollations() WHERE name = @name", connection);
        command.Parameters.AddWithValue("@name", collation);
        return (int)(await command.ExecuteScalarAsync())! > 0;
    }

    private static async Task<DedupKeyColumn> ReadDedupKeyColumnAsync(SqlConnection connection)
    {
        const string sql = """
            SELECT t.name, c.max_length, c.is_nullable, c.collation_name
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@table, N'U') AND c.name = N'Dedup_Key'
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@table", InboxTable);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "缺少 Dedup_Key 列");
        return new DedupKeyColumn(reader.GetString(0), reader.GetInt16(1), reader.GetBoolean(2), reader.GetString(3));
    }

    private static async Task<List<string>> ReadIndexesAsync(SqlConnection connection)
    {
        const string sql = """
            SELECT i.name, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.type_desc,
                   STRING_AGG(c.name + CASE WHEN ic.is_included_column = 1 THEN N'+' WHEN ic.is_descending_key = 1 THEN N'-' ELSE N'' END COLLATE DATABASE_DEFAULT, N',')
                       WITHIN GROUP (ORDER BY ic.is_included_column, ic.key_ordinal, c.name)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@table, N'U')
            GROUP BY i.name, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.type_desc
            ORDER BY i.name
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@table", InboxTable);
        await using var reader = await command.ExecuteReaderAsync();
        var indexes = new List<string>();
        while (await reader.ReadAsync())
        {
            var kind = reader.GetBoolean(2) ? "primary" : reader.GetBoolean(3) ? "constraint" : reader.GetBoolean(1) ? "unique" : "index";
            indexes.Add($"{reader.GetString(0)}|{kind}|{reader.GetString(4)}|{reader.GetString(5)}");
        }

        return indexes;
    }

    private static async Task<List<string>> ReadIndexOptionsAsync(SqlConnection connection)
    {
        const string sql = """
            SELECT i.name, i.ignore_dup_key, i.fill_factor, i.is_padded, p.data_compression_desc, ds.name, ISNULL(i.filter_definition, N'')
            FROM sys.indexes i
            JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id
            JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE i.object_id = OBJECT_ID(@table, N'U') AND i.name IS NOT NULL
            ORDER BY i.name
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@table", InboxTable);
        await using var reader = await command.ExecuteReaderAsync();
        var options = new List<string>();
        while (await reader.ReadAsync())
        {
            options.Add($"{reader.GetString(0)}|{(reader.GetBoolean(1) ? 1 : 0)}|{reader.GetByte(2)}|{(reader.GetBoolean(3) ? 1 : 0)}|{reader.GetString(4)}|{reader.GetString(5)}|{reader.GetString(6)}");
        }

        return options;
    }

    private static async Task<List<string>> ReadRowsAsync(SqlConnection connection)
    {
        const string sql = """
            SELECT CONVERT(nvarchar(36), Basic_Id) + N'|' + Dedup_Key COLLATE DATABASE_DEFAULT + N'|' + ISNULL(Message_Id, N'') + N'|' + Event_Name + N'|' + CONVERT(nvarchar(10), Status)
            FROM Sys_Event_Inbox
            ORDER BY Basic_Id
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task<DateTime> ReadTableModifyDateAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT modify_date FROM sys.objects WHERE object_id = OBJECT_ID(@table, N'U')", connection);
        command.Parameters.AddWithValue("@table", InboxTable);
        return (DateTime)(await command.ExecuteScalarAsync())!;
    }

    private static string ResolveRepairScriptPath([CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("无法解析测试源文件目录。");

        return Path.GetFullPath(Path.Combine(
            testDirectory, "..", "..", "..", "scripts", "upgrade", "mssql", "sys-event-inbox-dedup-key-collation.sql"));
    }

    private sealed record DedupKeyColumn(string TypeName, short MaxLength, bool IsNullable, string Collation);
}
