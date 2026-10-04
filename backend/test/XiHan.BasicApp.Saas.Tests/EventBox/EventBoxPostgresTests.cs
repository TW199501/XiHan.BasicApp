// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using DataRow = System.Data.DataRow;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.EventBus;
using XiHan.Framework.EventBus.Abstractions.Distributed;

namespace XiHan.BasicApp.Saas.Tests.EventBox;

/// <summary>
/// 收发件箱在 PostgreSQL 上的集成测试，未设置 XIHAN_TEST_POSTGRES 时跳过
/// </summary>
/// <remarks>
/// 每个测试实例重建收发件箱两张表，结束时只删除这两张表。
/// </remarks>
public sealed class EventBoxPostgresTests : IDisposable
{
    private const string ConnectionStringVariable = "XIHAN_TEST_POSTGRES";

    private readonly EventBoxTestContext? _context;

    /// <summary>
    /// 设置了连接串时重建收发件箱表
    /// </summary>
    public EventBoxPostgresTests()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true
        });
        DropEventTables(client);
        client.CodeFirst.InitTables<SysEventOutbox, SysEventInbox>();

        _context = new EventBoxTestContext(client);
    }

    /// <summary>
    /// 时间列为 timestamp with time zone，事件数据列为 bytea，事件名称列长 512，收件箱带完结时刻索引
    /// </summary>
    [Fact]
    public async Task InitTables_CreatesExpectedColumnTypesAndIndexes()
    {
        var context = RequireContext();
        var outboxTable = context.Client.EntityMaintenance.GetTableName<SysEventOutbox>();
        var inboxTable = context.Client.EntityMaintenance.GetTableName<SysEventInbox>();

        var outboxColumns = await GetColumnsAsync(context.Client, outboxTable);
        var inboxColumns = await GetColumnsAsync(context.Client, inboxTable);

        foreach (var columns in new[] { outboxColumns, inboxColumns })
        {
            Assert.Equal("timestamp with time zone", columns["created_time"].DataType);
            Assert.Equal("timestamp with time zone", columns["claim_time"].DataType);
            Assert.Equal("bytea", columns["event_data"].DataType);
            Assert.Equal(512, columns["event_name"].MaxLength);
        }

        Assert.Equal("timestamp with time zone", inboxColumns["handled_time"].DataType);
        Assert.Equal("timestamp with time zone", inboxColumns["next_retry_time"].DataType);

        var inboxIndexes = await GetIndexNamesAsync(context.Client, inboxTable);
        Assert.Contains($"ix_{inboxTable}_st_hati".ToLowerInvariant(), inboxIndexes);
        Assert.Contains($"ix_{inboxTable}_st_crti".ToLowerInvariant(), inboxIndexes);
        Assert.Contains($"ux_{inboxTable}_deke".ToLowerInvariant(), inboxIndexes);
    }

    /// <summary>
    /// 事务回滚后发件箱不留下事件
    /// </summary>
    [Fact]
    public async Task OutboxEnqueue_InRolledBackTransaction_LeavesNoRow()
    {
        var context = RequireContext();
        var outbox = context.CreateOutbox();

        context.Client.Ado.BeginTran();
        await outbox.EnqueueAsync(NewOutgoingEvent(context));
        context.Client.Ado.RollbackTran();

        Assert.Equal(0, await context.Client.Queryable<SysEventOutbox>().CountAsync());
    }

    /// <summary>
    /// 已领取的记录在超时前不可再领取，超时后可重新领取
    /// </summary>
    [Fact]
    public async Task OutboxClaim_ReclaimsOnlyAfterClaimTimeout()
    {
        var context = RequireContext();
        var outbox = context.CreateOutbox(new SaasEventBoxOptions { ClaimTimeout = TimeSpan.FromMinutes(5) });
        var info = NewOutgoingEvent(context);
        await outbox.EnqueueAsync(info);

        Assert.Equal(info.Id, Assert.Single(await outbox.GetWaitingEventsAsync(10)).Id);
        Assert.Empty(await outbox.GetWaitingEventsAsync(10));

        context.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var reclaimed = Assert.Single(await outbox.GetWaitingEventsAsync(10));

        Assert.Equal(info.Id, reclaimed.Id);
        Assert.Equal(info.EventName, reclaimed.EventName);
        Assert.Equal(info.EventData, reclaimed.EventData);
    }

    /// <summary>
    /// 事务外重复消息标识被忽略；事务内重复时抛出原始唯一约束异常
    /// </summary>
    [Fact]
    public async Task InboxEnqueue_DuplicateMessageId_IgnoredOutsideTransactionAndThrowsInside()
    {
        var context = RequireContext();
        var inbox = context.CreateInbox();

        await inbox.EnqueueAsync(NewIncomingEvent(context, "pg-dup"));
        await inbox.EnqueueAsync(NewIncomingEvent(context, "pg-dup"));

        Assert.Equal(1, await context.Client.Queryable<SysEventInbox>().CountAsync());

        context.Client.Ado.BeginTran();
        try
        {
            var exception = await Assert.ThrowsAnyAsync<Exception>(() => inbox.EnqueueAsync(NewIncomingEvent(context, "pg-dup")));
            Assert.Contains("23505", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            context.Client.Ado.RollbackTran();
        }

        Assert.Equal(1, await context.Client.Queryable<SysEventInbox>().CountAsync());
    }

    /// <summary>
    /// 只清理超过保留期的已完结记录
    /// </summary>
    [Fact]
    public async Task InboxDeleteOldEvents_RemovesOnlyExpiredHandledRows()
    {
        var context = RequireContext();
        var inbox = context.CreateInbox(new SaasEventBoxOptions { InboxRetentionPeriod = TimeSpan.FromDays(7) });
        var expired = NewIncomingEvent(context, "pg-old");
        await inbox.EnqueueAsync(expired);
        await inbox.GetWaitingEventsAsync(10);
        await inbox.MarkAsProcessedAsync(expired.Id);

        context.Clock.Advance(TimeSpan.FromDays(8));

        var recent = NewIncomingEvent(context, "pg-recent");
        await inbox.EnqueueAsync(recent);
        await inbox.GetWaitingEventsAsync(10);
        await inbox.MarkAsDiscardAsync(recent.Id);
        var pending = NewIncomingEvent(context, "pg-pending");
        await inbox.EnqueueAsync(pending);

        await inbox.DeleteOldEventsAsync();

        var ids = await context.Client.Queryable<SysEventInbox>().Select(e => e.BasicId).ToListAsync();
        Assert.DoesNotContain(expired.Id, ids);
        Assert.Contains(recent.Id, ids);
        Assert.Contains(pending.Id, ids);
    }

    /// <summary>
    /// 删除本实例建立的收发件箱表并释放上下文
    /// </summary>
    public void Dispose()
    {
        if (_context is null)
        {
            return;
        }

        DropEventTables(_context.Client);
        _context.Dispose();
    }

    private static void DropEventTables(ISqlSugarClient client)
    {
        foreach (var tableName in new[]
        {
            client.EntityMaintenance.GetTableName<SysEventOutbox>(),
            client.EntityMaintenance.GetTableName<SysEventInbox>()
        })
        {
            if (client.DbMaintenance.IsAnyTable(tableName, false))
            {
                client.DbMaintenance.DropTable(tableName);
            }
        }
    }

    private static async Task<Dictionary<string, (string DataType, int? MaxLength)>> GetColumnsAsync(ISqlSugarClient client, string tableName)
    {
        var table = await client.Ado.GetDataTableAsync(
            "SELECT column_name, data_type, character_maximum_length FROM information_schema.columns " +
            "WHERE table_schema = current_schema() AND lower(table_name) = lower(@tableName)",
            new { tableName });

        var columns = new Dictionary<string, (string DataType, int? MaxLength)>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow row in table.Rows)
        {
            var maxLength = row["character_maximum_length"] is DBNull ? (int?)null : Convert.ToInt32(row["character_maximum_length"]);
            columns[(string)row["column_name"]] = ((string)row["data_type"], maxLength);
        }

        Assert.NotEmpty(columns);
        return columns;
    }

    private static async Task<List<string>> GetIndexNamesAsync(ISqlSugarClient client, string tableName)
    {
        var table = await client.Ado.GetDataTableAsync(
            "SELECT indexname FROM pg_indexes WHERE schemaname = current_schema() AND lower(tablename) = lower(@tableName)",
            new { tableName });

        return [.. table.Rows.Cast<DataRow>().Select(row => ((string)row["indexname"]).ToLowerInvariant())];
    }

    private static OutgoingEventInfo NewOutgoingEvent(EventBoxTestContext context)
    {
        context.Clock.Advance(TimeSpan.FromMilliseconds(1));
        return new OutgoingEventInfo(Guid.NewGuid(), "order.created", [1, 2, 3], context.Clock.GetUtcNow().UtcDateTime);
    }

    private static IncomingEventInfo NewIncomingEvent(EventBoxTestContext context, string messageId)
    {
        context.Clock.Advance(TimeSpan.FromMilliseconds(1));
        return new IncomingEventInfo(Guid.NewGuid(), messageId, "order.created", [1, 2, 3], context.Clock.GetUtcNow().UtcDateTime);
    }

    private EventBoxTestContext RequireContext()
    {
        Assert.SkipWhen(_context is null, $"未设置 {ConnectionStringVariable}");
        return _context;
    }
}
