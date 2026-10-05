// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using SqlSugar;
using XiHan.BasicApp.Core.Data;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.Upgrade;
using XiHan.BasicApp.Saas.Tests.TestDatabases;
using DataRow = System.Data.DataRow;

namespace XiHan.BasicApp.Saas.Tests.Upgrade;

/// <summary>
/// 5.6.1 升级脚本经正式迁移执行器在真实数据库上执行：旧结构被改正，再次执行不做任何改动
/// </summary>
/// <remarks>
/// 各数据库以环境变量门控，未设置即跳过。MySQL 与 SQL Server 用例各自新建临时库，结束时删除。
/// </remarks>
public sealed class UpgradeScript561DatabaseTests
{
    private const string ScriptVersion = "5.6.1";

    private const string OutboxTable = "Sys_Event_Outbox";

    private const string InboxTable = "Sys_Event_Inbox";

    private const string IdempotencyTable = "Sys_Idempotency_Record";

    private static readonly Dictionary<string, string[]> MySqlTimeColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        [OutboxTable] = ["Created_Time", "Claim_Time"],
        [InboxTable] = ["Created_Time", "Next_Retry_Time", "Claim_Time", "Handled_Time"],
        [IdempotencyTable] = ["Lease_Expires_Time", "Expires_Time", "Created_Time", "Completed_Time"]
    };

    [Fact]
    public async Task PostgreSql_脚本可重复执行()
    {
        using var db = CreateClient(DbType.PostgreSQL, RequireConnectionString(IntegrationDatabase.PostgresVariable));

        await ExecuteScriptAsync(db, UpgradeScriptDialect.PostgreSql);
        await ExecuteScriptAsync(db, UpgradeScriptDialect.PostgreSql);
    }

    [Fact]
    public async Task MySql_旧结构改为微秒时间列与区分大小写的去重键_再次执行不做改动()
    {
        await using var database = await MySqlScratchDatabase.CreateAsync(RequireConnectionString(IntegrationDatabase.MySqlVariable));
        using (var legacy = CreateLegacyClient(DbType.MySql, database.ConnectionString))
        {
            legacy.CodeFirst.InitTables(typeof(SysEventOutbox), typeof(SysEventInbox), typeof(SysIdempotencyRecord));
            _ = await legacy.Insertable(new List<SysEventInbox> { NewInboxRow("Msg-A"), NewInboxRow("消息-1") }).ExecuteCommandAsync();
        }

        using var db = CreateClient(DbType.MySql, database.ConnectionString);
        var columnsBefore = await ReadMySqlColumnsAsync(db);
        var indexesBefore = await ReadIndexesAsync(db);
        var rowsBefore = await ReadMySqlInboxRowsAsync(db);
        var tableIdsBefore = await ReadMySqlTableIdsAsync(db);
        foreach (var (table, columns) in MySqlTimeColumns)
        {
            Assert.All(columns, column => Assert.Equal("datetime", columnsBefore[$"{table}.{column}"].Type));
        }

        Assert.Equal("utf8mb4_0900_ai_ci", columnsBefore[$"{InboxTable}.Dedup_Key"].Collation);

        await ExecuteScriptAsync(db, UpgradeScriptDialect.MySql);

        var columnsAfter = await ReadMySqlColumnsAsync(db);
        Assert.Equal(ExpectedMySqlColumns(columnsBefore), columnsAfter);
        Assert.Equal(indexesBefore, await ReadIndexesAsync(db));
        Assert.Equal(rowsBefore, await ReadMySqlInboxRowsAsync(db));
        var tableIdsAfter = await ReadMySqlTableIdsAsync(db);
        Assert.All(tableIdsBefore.Keys, table => Assert.NotEqual(tableIdsBefore[table], tableIdsAfter[table]));
        Assert.Equal(0, await CountMySqlRoutinesAsync(db));

        await ExecuteScriptAsync(db, UpgradeScriptDialect.MySql);

        Assert.Equal(columnsAfter, await ReadMySqlColumnsAsync(db));
        Assert.Equal(indexesBefore, await ReadIndexesAsync(db));
        Assert.Equal(rowsBefore, await ReadMySqlInboxRowsAsync(db));
        Assert.Equal(tableIdsAfter, await ReadMySqlTableIdsAsync(db));
        Assert.Equal(0, await CountMySqlRoutinesAsync(db));

        _ = await db.Insertable(NewInboxRow("msg-a")).ExecuteCommandAsync();
        Assert.Equal(3, (await ReadMySqlInboxRowsAsync(db)).Count);
    }

    [Fact]
    public async Task SqlServer_旧去重键改为nvarchar与区分大小写的排序规则_时间列不动_再次执行不做改动()
    {
        await using var database = await SqlServerScratchDatabase.CreateAsync(RequireConnectionString(IntegrationDatabase.SqlServerVariable), "Latin1_General_CI_AS");
        using (var legacy = CreateLegacyClient(DbType.SqlServer, database.ConnectionString))
        {
            legacy.CodeFirst.InitTables(typeof(SysEventOutbox), typeof(SysEventInbox), typeof(SysIdempotencyRecord));
            _ = await legacy.Insertable(new List<SysEventInbox> { NewInboxRow("Msg-A"), NewInboxRow("Msg-B") }).ExecuteCommandAsync();
        }

        using var db = CreateClient(DbType.SqlServer, database.ConnectionString);
        var timeColumnsBefore = await ReadSqlServerTimeColumnsAsync(db);
        var indexesBefore = await ReadIndexesAsync(db);
        var rowsBefore = await ReadSqlServerInboxRowsAsync(db);
        Assert.Equal("varchar|256|Latin1_General_CI_AS", await ReadSqlServerDedupKeyAsync(db));
        Assert.Equal(10, timeColumnsBefore.Count);
        Assert.All(timeColumnsBefore, column => Assert.EndsWith("|datetimeoffset|7", column));

        await ExecuteScriptAsync(db, UpgradeScriptDialect.SqlServer);

        Assert.Equal("nvarchar|512|Latin1_General_CS_AS_KS_WS", await ReadSqlServerDedupKeyAsync(db));
        Assert.Equal(timeColumnsBefore, await ReadSqlServerTimeColumnsAsync(db));
        Assert.Equal(indexesBefore, await ReadIndexesAsync(db));
        Assert.Equal(rowsBefore, await ReadSqlServerInboxRowsAsync(db));
        var modified = await ReadSqlServerInboxModifyDateAsync(db);

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await ExecuteScriptAsync(db, UpgradeScriptDialect.SqlServer);

        Assert.Equal("nvarchar|512|Latin1_General_CS_AS_KS_WS", await ReadSqlServerDedupKeyAsync(db));
        Assert.Equal(indexesBefore, await ReadIndexesAsync(db));
        Assert.Equal(rowsBefore, await ReadSqlServerInboxRowsAsync(db));
        Assert.Equal(modified, await ReadSqlServerInboxModifyDateAsync(db));

        _ = await db.Insertable(NewInboxRow("msg-a")).ExecuteCommandAsync();
        Assert.Equal(3, (await ReadSqlServerInboxRowsAsync(db)).Count);
    }

    private static string RequireConnectionString(string variable)
    {
        var connectionString = IntegrationDatabase.GetConnectionString(variable);
        if (connectionString is null)
        {
            Assert.Skip($"未设置 {variable}，跳过。");
        }

        return connectionString;
    }

    private static SqlSugarClient CreateClient(DbType dbType, string connectionString)
    {
        var config = IntegrationDatabase.CreateConnectionConfig(dbType, connectionString);
        config.ConfigId = $"E87_561_{dbType}_{Guid.NewGuid():N}";
        return new SqlSugarClient(config);
    }

    private static SqlSugarClient CreateLegacyClient(DbType dbType, string connectionString)
    {
        var config = new ConnectionConfig
        {
            ConfigId = $"E87_561_legacy_{Guid.NewGuid():N}",
            ConnectionString = connectionString,
            DbType = dbType,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityService = (property, column) =>
                {
                    if (property.PropertyType == typeof(DateTimeOffset) || property.PropertyType == typeof(DateTimeOffset?))
                    {
                        column.Length = 0;
                    }
                }
            }
        };
        MySqlConnectionStrings.Apply(config);
        return new SqlSugarClient(config);
    }

    private static SysEventInbox NewInboxRow(string messageId)
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

    private static async Task ExecuteScriptAsync(ISqlSugarClient db, string dialect)
    {
        var sql = await File.ReadAllTextAsync(ResolveScriptPath(dialect));
        await new SaasUpgradeMigrationExecutor(UpgradeTestDoubles.CreateResolver(db)).ExecuteAsync(sql);
    }

    private static SortedDictionary<string, ColumnShape> ExpectedMySqlColumns(SortedDictionary<string, ColumnShape> before)
    {
        var expected = new SortedDictionary<string, ColumnShape>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, shape) in before)
        {
            var parts = key.Split('.');
            expected[key] = (parts[0], parts[1]) switch
            {
                var (table, column) when MySqlTimeColumns[table].Contains(column, StringComparer.OrdinalIgnoreCase) => shape with { Type = "datetime(6)" },
                var (table, column) when table.Equals(InboxTable, StringComparison.OrdinalIgnoreCase) && column.Equals("Dedup_Key", StringComparison.OrdinalIgnoreCase) => shape with { Collation = "utf8mb4_bin" },
                _ => shape
            };
        }

        return expected;
    }

    private static async Task<SortedDictionary<string, ColumnShape>> ReadMySqlColumnsAsync(ISqlSugarClient db)
    {
        var columns = new SortedDictionary<string, ColumnShape>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in MySqlTimeColumns.Keys)
        {
            var data = await db.Ado.GetDataTableAsync(
                "SELECT COLUMN_NAME AS column_name, COLUMN_TYPE AS column_type, IS_NULLABLE AS is_nullable, " +
                "COLLATION_NAME AS collation_name, COLUMN_COMMENT AS column_comment " +
                "FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @tableName",
                new { tableName = table });
            foreach (DataRow row in data.Rows)
            {
                columns[$"{table}.{Text(row, "column_name")}"] = new ColumnShape(
                    Text(row, "column_type")!,
                    Text(row, "is_nullable")!,
                    Text(row, "collation_name"),
                    Text(row, "column_comment") ?? string.Empty);
            }
        }

        Assert.NotEmpty(columns);
        return columns;
    }

    private static async Task<List<string>> ReadMySqlInboxRowsAsync(ISqlSugarClient db)
    {
        var rows = await db.Ado.SqlQueryAsync<string>(
            $"SELECT CONCAT(Dedup_Key, '|', DATE_FORMAT(Created_Time, '%Y-%m-%d %H:%i:%s')) FROM {InboxTable}");
        return [.. rows.Order(StringComparer.Ordinal)];
    }

    private static async Task<Dictionary<string, long>> ReadMySqlTableIdsAsync(ISqlSugarClient db)
    {
        var tableIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in MySqlTimeColumns.Keys)
        {
            var value = await db.Ado.GetScalarAsync(
                "SELECT TABLE_ID FROM information_schema.INNODB_TABLES WHERE NAME = CONCAT(DATABASE(), '/', @tableName)",
                new { tableName = table });
            tableIds[table] = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        return tableIds;
    }

    private static async Task<int> CountMySqlRoutinesAsync(ISqlSugarClient db)
    {
        return await db.Ado.GetIntAsync("SELECT COUNT(*) FROM information_schema.routines WHERE routine_schema = DATABASE()");
    }

    private static async Task<List<string>> ReadIndexesAsync(ISqlSugarClient db)
    {
        var indexes = new List<string>();
        foreach (var table in MySqlTimeColumns.Keys)
        {
            foreach (var index in (await DatabaseSchemaProbe.GetIndexesAsync(db, table)).Values)
            {
                indexes.Add($"{table}|{index.Name}|{index.IsUnique}|{string.Join(",", index.Columns)}");
            }
        }

        return [.. indexes.Order(StringComparer.Ordinal)];
    }

    private static async Task<string> ReadSqlServerDedupKeyAsync(ISqlSugarClient db)
    {
        return await db.Ado.GetStringAsync(
            "SELECT CONCAT(t.name, N'|', c.max_length, N'|', c.collation_name) " +
            "FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
            $"WHERE c.object_id = OBJECT_ID(N'{InboxTable}', N'U') AND c.name = N'Dedup_Key'");
    }

    private static async Task<List<string>> ReadSqlServerTimeColumnsAsync(ISqlSugarClient db)
    {
        var columns = await db.Ado.SqlQueryAsync<string>(
            "SELECT CONCAT(OBJECT_NAME(c.object_id), N'.', c.name, N'|', t.name, N'|', c.scale) " +
            "FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
            $"WHERE t.name = N'datetimeoffset' AND OBJECT_NAME(c.object_id) IN (N'{OutboxTable}', N'{InboxTable}', N'{IdempotencyTable}')");
        return [.. columns.Order(StringComparer.Ordinal)];
    }

    private static async Task<List<string>> ReadSqlServerInboxRowsAsync(ISqlSugarClient db)
    {
        var rows = await db.Ado.SqlQueryAsync<string>($"SELECT Dedup_Key FROM {InboxTable}");
        return [.. rows.Order(StringComparer.Ordinal)];
    }

    private static async Task<string> ReadSqlServerInboxModifyDateAsync(ISqlSugarClient db)
    {
        return await db.Ado.GetStringAsync(
            $"SELECT CONVERT(varchar(30), modify_date, 121) FROM sys.objects WHERE object_id = OBJECT_ID(N'{InboxTable}', N'U')");
    }

    private static string? Text(DataRow row, string column)
    {
        return row[column] switch
        {
            DBNull => null,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            var value => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static string ResolveScriptPath(string dialect, [CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("无法解析测试源文件目录。");

        return Path.GetFullPath(Path.Combine(
            testDirectory, "..", "..", "..", "src", "main", "XiHan.BasicApp.WebHost", "UpdateScripts", ScriptVersion, dialect, $"{ScriptVersion}.sql"));
    }

    /// <summary>
    /// MySQL 列定义
    /// </summary>
    /// <param name="Type">列类型，如 datetime(6)</param>
    /// <param name="Nullable">YES 或 NO</param>
    /// <param name="Collation">排序规则；非字符列为 null</param>
    /// <param name="Comment">列注释</param>
    private sealed record ColumnShape(string Type, string Nullable, string? Collation, string Comment);

    /// <summary>
    /// 默认排序规则为 utf8mb4_0900_ai_ci 的 MySQL 临时库，释放时删除
    /// </summary>
    private sealed class MySqlScratchDatabase : IAsyncDisposable
    {
        private readonly string _serverConnectionString;

        private readonly string _name;

        private MySqlScratchDatabase(string serverConnectionString, string name, string connectionString)
        {
            _serverConnectionString = serverConnectionString;
            _name = name;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<MySqlScratchDatabase> CreateAsync(string connectionString)
        {
            var name = $"xihan_e87_{Guid.NewGuid():N}";
            var server = new MySqlConnectionStringBuilder(connectionString) { Database = string.Empty }.ConnectionString;
            await ExecuteAsync(server, $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");

            var database = new MySqlConnectionStringBuilder(connectionString) { Database = name }.ConnectionString;
            return new MySqlScratchDatabase(server, name, database);
        }

        public async ValueTask DisposeAsync()
        {
            await ExecuteAsync(_serverConnectionString, $"DROP DATABASE IF EXISTS `{_name}`");
        }

        private static async Task ExecuteAsync(string connectionString, string sql)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            _ = await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// 指定默认排序规则的 SQL Server 临时库，释放时删除
    /// </summary>
    private sealed class SqlServerScratchDatabase : IAsyncDisposable
    {
        private readonly string _serverConnectionString;

        private readonly string _name;

        private SqlServerScratchDatabase(string serverConnectionString, string name, string connectionString)
        {
            _serverConnectionString = serverConnectionString;
            _name = name;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<SqlServerScratchDatabase> CreateAsync(string connectionString, string collation)
        {
            var name = $"xihan_e87_{Guid.NewGuid():N}";
            var server = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
            await ExecuteAsync(server, $"CREATE DATABASE [{name}] COLLATE {collation}");

            var database = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = name }.ConnectionString;
            return new SqlServerScratchDatabase(server, name, database);
        }

        public async ValueTask DisposeAsync()
        {
            await using (var pooled = new SqlConnection(ConnectionString))
            {
                SqlConnection.ClearPool(pooled);
            }

            await ExecuteAsync(
                _serverConnectionString,
                $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];");
        }

        private static async Task ExecuteAsync(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            _ = await command.ExecuteNonQueryAsync();
        }
    }
}
