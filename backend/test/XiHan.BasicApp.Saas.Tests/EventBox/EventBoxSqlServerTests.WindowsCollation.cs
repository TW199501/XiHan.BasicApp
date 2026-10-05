// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Data.SqlClient;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.EventBus;
using XiHan.Framework.EventBus.Abstractions.Distributed;

namespace XiHan.BasicApp.Saas.Tests.EventBox;

/// <summary>
/// 默认排序规则为 Windows 排序规则的 SQL Server 数据库上的去重键测试
/// </summary>
/// <remarks>
/// 每个用例新建一个默认排序规则为 <see cref="WindowsCollation"/> 的临时数据库，结束时删除。
/// </remarks>
public sealed partial class EventBoxSqlServerTests
{
    private const string WindowsCollation = "Chinese_PRC_CI_AS";

    private const string WindowsDedupKeyCollation = "Chinese_PRC_CS_AS_KS_WS";

    /// <summary>
    /// Windows 默认排序规则有 CS_AS_KS_WS 版本时，建表使用它；只差大小写、全半角、非拉丁字符的消息标识都各自入箱
    /// </summary>
    [Fact]
    public async Task WindowsDefaultCollation_InitTablesUsesKanaAndWidthSensitiveCollation()
    {
        await using var database = await ScratchDatabase.CreateAsync(RequireConnectionString(), WindowsCollation);
        var config = new ConnectionConfig
        {
            ConfigId = $"windows-{Guid.NewGuid():N}",
            ConnectionString = database.ConnectionString,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute
        };
        SaasEventBoxCodeFirstConvention.Apply(config);
        using var context = new EventBoxTestContext(new SqlSugarClient(config));
        context.Client.CodeFirst.InitTables<SysEventInbox>();

        await using var connection = await OpenConnectionAsync(database.ConnectionString);
        Assert.Equal(WindowsCollation, await ReadDatabaseCollationAsync(connection));
        Assert.Equal(new DedupKeyColumn("nvarchar", 512, false, WindowsDedupKeyCollation), await ReadDedupKeyColumnAsync(connection));

        var inbox = context.CreateInbox();
        string[] messageIds = ["Msg-A", "msg-a", "A-1", "Ａ-1", "消息-1", "訊息-1"];
        foreach (var messageId in messageIds)
        {
            context.Clock.Advance(TimeSpan.FromMilliseconds(1));
            await inbox.EnqueueAsync(new IncomingEventInfo(Guid.NewGuid(), messageId, "order.created", [1, 2, 3], context.Clock.GetUtcNow().UtcDateTime));
        }

        var keys = await context.Client.Queryable<SysEventInbox>().Select(e => e.DedupKey).ToListAsync();
        Assert.Equal(messageIds.Order(StringComparer.Ordinal), keys.Order(StringComparer.Ordinal));
        Assert.False(await inbox.ExistsByMessageIdAsync("MSG-A"));
    }

    /// <summary>
    /// Windows 默认排序规则的旧表经修复脚本改为 nvarchar 与 CS_AS_KS_WS，再次执行不做任何改动
    /// </summary>
    [Fact]
    public async Task WindowsDefaultCollation_RepairScriptConvertsLegacyColumn()
    {
        await using var database = await ScratchDatabase.CreateAsync(RequireConnectionString(), WindowsCollation);
        using var legacy = CreateLegacyClient(database.ConnectionString);
        legacy.CodeFirst.InitTables<SysEventInbox>();
        await legacy.Insertable(new List<SysEventInbox> { NewLegacyRow("Msg-A"), NewLegacyRow("A-1") }).ExecuteCommandAsync();

        await using var connection = await OpenConnectionAsync(database.ConnectionString);
        var columnBefore = await ReadDedupKeyColumnAsync(connection);
        var indexesBefore = await ReadIndexesAsync(connection);
        var rowsBefore = await ReadRowsAsync(connection);
        Assert.Equal(new DedupKeyColumn("varchar", 256, false, WindowsCollation), columnBefore);

        await ExecuteRepairScriptAsync(connection);

        var columnAfter = await ReadDedupKeyColumnAsync(connection);
        Assert.Equal(new DedupKeyColumn("nvarchar", 512, false, WindowsDedupKeyCollation), columnAfter);
        Assert.Equal(indexesBefore, await ReadIndexesAsync(connection));
        Assert.Equal(rowsBefore, await ReadRowsAsync(connection));
        var modified = await ReadTableModifyDateAsync(connection);

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await ExecuteRepairScriptAsync(connection);

        Assert.Equal(columnAfter, await ReadDedupKeyColumnAsync(connection));
        Assert.Equal(indexesBefore, await ReadIndexesAsync(connection));
        Assert.Equal(rowsBefore, await ReadRowsAsync(connection));
        Assert.Equal(modified, await ReadTableModifyDateAsync(connection));

        await legacy.Insertable(new List<SysEventInbox> { NewLegacyRow("msg-a"), NewLegacyRow("Ａ-1") }).ExecuteCommandAsync();
        Assert.Equal(4, (await ReadRowsAsync(connection)).Count);
    }

    /// <summary>
    /// 指定默认排序规则的临时数据库，释放时删除
    /// </summary>
    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private readonly string _serverConnectionString;

        private readonly string _name;

        private ScratchDatabase(string serverConnectionString, string name, string connectionString)
        {
            _serverConnectionString = serverConnectionString;
            _name = name;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<ScratchDatabase> CreateAsync(string connectionString, string collation)
        {
            var name = $"xihan_e85_{Guid.NewGuid():N}";
            var server = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
            await using (var connection = await OpenConnectionAsync(server))
            {
                await using var command = new SqlCommand($"CREATE DATABASE [{name}] COLLATE {collation}", connection);
                await command.ExecuteNonQueryAsync();
            }

            var database = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = name }.ConnectionString;
            return new ScratchDatabase(server, name, database);
        }

        public async ValueTask DisposeAsync()
        {
            await using (var pooled = new SqlConnection(ConnectionString))
            {
                SqlConnection.ClearPool(pooled);
            }

            await using var connection = await OpenConnectionAsync(_serverConnectionString);
            await using var command = new SqlCommand(
                $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];",
                connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
