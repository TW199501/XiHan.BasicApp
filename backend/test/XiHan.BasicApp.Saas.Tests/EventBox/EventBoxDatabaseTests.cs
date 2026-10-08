// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.EventBus;
using XiHan.BasicApp.Saas.Tests.TestDatabases;
using XiHan.Framework.EventBus.Abstractions.Distributed;

namespace XiHan.BasicApp.Saas.Tests.EventBox;

/// <summary>
/// 收发件箱在真实数据库上的集成测试基类，未设置对应连接串环境变量时跳过
/// </summary>
/// <remarks>
/// 每个测试实例重建收发件箱两张表，结束时只删除这两张表。
/// </remarks>
public abstract class EventBoxDatabaseTests : IDisposable
{
    private readonly string? _connectionString;

    private readonly EventBoxTestContext? _context;

    private const int ConcurrentEventCount = 200;

    private const int ConcurrentClaimerCount = 4;

    private const int ConcurrentBatchSize = 10;

    /// <summary>
    /// 设置了连接串时重建收发件箱表
    /// </summary>
    /// <param name="connectionStringVariable">连接串环境变量名</param>
    /// <param name="databaseType">数据库种类</param>
    protected EventBoxDatabaseTests(string connectionStringVariable, DbType databaseType)
    {
        ConnectionStringVariable = connectionStringVariable;
        DatabaseType = databaseType;
        _connectionString = IntegrationDatabase.GetConnectionString(connectionStringVariable);
        if (_connectionString is null)
        {
            return;
        }

        var client = IntegrationDatabase.CreateClient(databaseType, _connectionString);
        DropEventTables(client);
        client.CodeFirst.InitTables<SysEventOutbox, SysEventInbox>();

        _context = new EventBoxTestContext(client);
    }

    /// <summary>
    /// 连接串环境变量名
    /// </summary>
    protected string ConnectionStringVariable { get; }

    /// <summary>
    /// 数据库种类
    /// </summary>
    protected DbType DatabaseType { get; }

    /// <summary>
    /// 时间列（DateTimeOffset）的数据类型
    /// </summary>
    protected abstract string ExpectedTimestampType { get; }

    /// <summary>
    /// 二进制列（byte[]）的数据类型
    /// </summary>
    protected abstract string ExpectedBinaryType { get; }

    /// <summary>
    /// 二进制列的最大长度
    /// </summary>
    protected abstract long? ExpectedBinaryMaxLength { get; }

    /// <summary>
    /// 去重键列的排序规则，使用数据库默认值时为 null
    /// </summary>
    protected abstract string? ExpectedDedupKeyCollation { get; }

    /// <summary>
    /// 去重键列的数据类型
    /// </summary>
    protected abstract string ExpectedDedupKeyType { get; }

    /// <summary>
    /// 时间列读回时是否保留写入的偏移
    /// </summary>
    protected abstract bool PreservesOffset { get; }

    /// <summary>
    /// 时间列、二进制列与事件名称列的定义符合该数据库的预期，收件箱带三个索引
    /// </summary>
    [Fact]
    public async Task InitTables_CreatesExpectedColumnTypesAndIndexes()
    {
        var context = RequireContext();
        var outboxTable = context.Client.EntityMaintenance.GetTableName<SysEventOutbox>();
        var inboxTable = context.Client.EntityMaintenance.GetTableName<SysEventInbox>();

        var outboxColumns = await DatabaseSchemaProbe.GetColumnsAsync(context.Client, outboxTable);
        var inboxColumns = await DatabaseSchemaProbe.GetColumnsAsync(context.Client, inboxTable);

        foreach (var columns in new[] { outboxColumns, inboxColumns })
        {
            Assert.Equal(ExpectedTimestampType, columns["created_time"].DataType);
            Assert.Equal(ExpectedTimestampType, columns["claim_time"].DataType);
            Assert.Equal(ExpectedBinaryType, columns["event_data"].DataType);
            Assert.Equal(ExpectedBinaryMaxLength, columns["event_data"].MaxLength);
            Assert.Equal(512, columns["event_name"].MaxLength);
        }

        Assert.Equal(ExpectedTimestampType, inboxColumns["handled_time"].DataType);
        Assert.Equal(ExpectedTimestampType, inboxColumns["next_retry_time"].DataType);

        var inboxIndexes = await DatabaseSchemaProbe.GetIndexesAsync(context.Client, inboxTable);
        Assert.False(RequireIndex(inboxIndexes, $"ix_{inboxTable}_st_hati").IsUnique);
        Assert.False(RequireIndex(inboxIndexes, $"ix_{inboxTable}_st_crti").IsUnique);
        var dedupIndex = RequireIndex(inboxIndexes, $"ux_{inboxTable}_deke");
        Assert.True(dedupIndex.IsUnique);
        Assert.Equal(new[] { "dedup_key" }, dedupIndex.Columns);
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
    /// 事务外重复消息标识被忽略；事务内重复时抛出该数据库的原始唯一约束异常，事务仍可回滚
    /// </summary>
    [Fact]
    public async Task InboxEnqueue_DuplicateMessageId_IgnoredOutsideTransactionAndThrowsInside()
    {
        var context = RequireContext();
        var inbox = context.CreateInbox();

        await inbox.EnqueueAsync(NewIncomingEvent(context, "db-dup"));
        await inbox.EnqueueAsync(NewIncomingEvent(context, "db-dup"));

        Assert.Equal(1, await context.Client.Queryable<SysEventInbox>().CountAsync());

        context.Client.Ado.BeginTran();
        try
        {
            var exception = await Assert.ThrowsAnyAsync<Exception>(() => inbox.EnqueueAsync(NewIncomingEvent(context, "db-dup")));
            Assert.True(IsDuplicateKeyViolation(exception), $"[{DatabaseType}] 预期唯一约束异常，实际为 {exception.GetType().FullName}");
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
        var expired = NewIncomingEvent(context, "db-old");
        await inbox.EnqueueAsync(expired);
        await inbox.GetWaitingEventsAsync(10);
        await inbox.MarkAsProcessedAsync(expired.Id);

        context.Clock.Advance(TimeSpan.FromDays(8));

        var recent = NewIncomingEvent(context, "db-recent");
        await inbox.EnqueueAsync(recent);
        await inbox.GetWaitingEventsAsync(10);
        await inbox.MarkAsDiscardAsync(recent.Id);
        var pending = NewIncomingEvent(context, "db-pending");
        await inbox.EnqueueAsync(pending);

        await inbox.DeleteOldEventsAsync();

        var ids = await context.Client.Queryable<SysEventInbox>().Select(e => e.BasicId).ToListAsync();
        Assert.DoesNotContain(expired.Id, ids);
        Assert.Contains(recent.Id, ids);
        Assert.Contains(pending.Id, ids);
    }

    /// <summary>
    /// 收发件箱全部时间列至少保留 6 位小数秒
    /// </summary>
    [Fact]
    public async Task InitTables_TimestampColumnsKeepSubSecondPrecision()
    {
        var context = RequireContext();
        var expected = new Dictionary<string, string[]>
        {
            [context.Client.EntityMaintenance.GetTableName<SysEventOutbox>()] = ["created_time", "claim_time"],
            [context.Client.EntityMaintenance.GetTableName<SysEventInbox>()] = ["created_time", "next_retry_time", "claim_time", "handled_time"]
        };

        foreach (var (tableName, columnNames) in expected)
        {
            var columns = await DatabaseSchemaProbe.GetColumnsAsync(context.Client, tableName);
            foreach (var columnName in columnNames)
            {
                var precision = columns[columnName].DateTimePrecision;
                Assert.True(precision >= 6, $"[{DatabaseType}] {tableName}.{columnName} 的小数秒位数为 {precision?.ToString() ?? "null"}，至少应为 6");
            }
        }
    }

    /// <summary>
    /// 带毫秒的创建时刻与领取时刻写入后读回完全相等
    /// </summary>
    [Fact]
    public async Task CreatedTime_RoundTripsExactInstant()
    {
        var context = RequireContext();
        var createdTime = new DateTime(2026, 10, 4, 5, 6, 7, 123, DateTimeKind.Utc);
        var outgoing = new OutgoingEventInfo(Guid.NewGuid(), "order.created", [1, 2, 3], createdTime);
        var incoming = new IncomingEventInfo(Guid.NewGuid(), "db-roundtrip", "order.created", [1, 2, 3], createdTime);
        var outbox = context.CreateOutbox();
        await outbox.EnqueueAsync(outgoing);
        await context.CreateInbox().EnqueueAsync(incoming);

        context.Clock.Advance(TimeSpan.FromMilliseconds(456));
        var claimTime = context.Clock.GetUtcNow();
        Assert.Single(await outbox.GetWaitingEventsAsync(10));

        var outboxRow = await context.Client.Queryable<SysEventOutbox>().Where(e => e.BasicId == outgoing.Id).FirstAsync();
        var inboxRow = await context.Client.Queryable<SysEventInbox>().Where(e => e.BasicId == incoming.Id).FirstAsync();
        Assert.Equal(createdTime, outboxRow.CreatedTime.UtcDateTime);
        Assert.Equal(createdTime, inboxRow.CreatedTime.UtcDateTime);
        Assert.Equal(claimTime.UtcDateTime, outboxRow.ClaimTime!.Value.UtcDateTime);
    }

    /// <summary>
    /// 以本机时区偏移表示的时刻作查询参数，筛出该时刻的事件
    /// </summary>
    [Fact]
    public async Task CreatedTimeFilter_AcceptsLocalOffsetParameter()
    {
        var context = RequireContext();
        var createdTime = new DateTimeOffset(2026, 10, 4, 5, 6, 7, 123, TimeSpan.Zero);
        var row = NewOutboxRow(createdTime);
        await context.Client.Insertable(row).ExecuteCommandAsync();
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(createdTime.UtcDateTime);
        var from = createdTime.AddSeconds(-1).ToOffset(localOffset);
        var to = createdTime.AddSeconds(1).ToOffset(localOffset);

        var rows = await context.Client.Queryable<SysEventOutbox>()
            .Where(e => e.CreatedTime >= from && e.CreatedTime <= to)
            .ToListAsync();

        Assert.Equal(row.BasicId, Assert.Single(rows).BasicId);
    }

    /// <summary>
    /// 非 UTC 偏移的时刻经实体插入与更新写入后，读回同一时刻
    /// </summary>
    [Fact]
    public async Task NonUtcOffsetTimes_WrittenByEntityRoundTripInstant()
    {
        var context = RequireContext();
        var createdTime = new DateTimeOffset(2026, 10, 4, 10, 6, 7, 123, TimeSpan.FromHours(5));
        var claimTime = createdTime.AddMinutes(30);
        var row = NewOutboxRow(createdTime);
        await context.Client.Insertable(row).ExecuteCommandAsync();
        row.ClaimTime = claimTime;
        await context.Client.Updateable(row).UpdateColumns(e => e.ClaimTime).ExecuteCommandAsync();

        var stored = await context.Client.Queryable<SysEventOutbox>().Where(e => e.BasicId == row.BasicId).FirstAsync();

        Assert.Equal(createdTime.UtcDateTime, stored.CreatedTime.UtcDateTime);
        Assert.Equal(claimTime.UtcDateTime, stored.ClaimTime!.Value.UtcDateTime);
    }

    /// <summary>
    /// 非 UTC 偏移与本机偏移的时刻经多行插入写入后，读回同一时刻
    /// </summary>
    [Fact]
    public async Task NonUtcOffsetTimes_InsertedAsListRoundTripInstant()
    {
        var context = RequireContext();
        var times = NonUtcOffsetTimes();
        var rows = times.Select(NewOutboxRow).ToList();

        await context.Client.Insertable(rows).ExecuteCommandAsync();

        var stored = await LoadOutboxRowsAsync(context, rows);
        for (var i = 0; i < times.Length; i++)
        {
            AssertSameTime(times[i], stored[i].CreatedTime);
        }
    }

    /// <summary>
    /// 非 UTC 偏移与本机偏移的时刻经多行更新写入后，读回同一时刻
    /// </summary>
    [Fact]
    public async Task NonUtcOffsetTimes_UpdatedAsListRoundTripInstant()
    {
        var context = RequireContext();
        var createdTime = new DateTimeOffset(2026, 10, 4, 5, 6, 7, 123, TimeSpan.Zero);
        var rows = new List<SysEventOutbox> { NewOutboxRow(createdTime), NewOutboxRow(createdTime) };
        foreach (var row in rows)
        {
            await context.Client.Insertable(row).ExecuteCommandAsync();
        }

        var times = NonUtcOffsetTimes();
        for (var i = 0; i < times.Length; i++)
        {
            rows[i].ClaimTime = times[i];
        }

        await context.Client.Updateable(rows).ExecuteCommandAsync();

        var stored = await LoadOutboxRowsAsync(context, rows);
        for (var i = 0; i < times.Length; i++)
        {
            AssertSameTime(times[i], stored[i].ClaimTime!.Value);
        }
    }

    /// <summary>
    /// 同一秒内先后入箱的事件按创建时刻领取，主键顺序与时间顺序相反
    /// </summary>
    [Fact]
    public async Task OutboxClaim_ReturnsOldestFirst()
    {
        var context = RequireContext();
        var outbox = context.CreateOutbox();
        var start = new DateTime(2026, 10, 4, 1, 0, 0, DateTimeKind.Utc);
        Guid[] ids =
        [
            Guid.Parse("ffffffff-0000-0000-0000-000000000000"),
            Guid.Parse("88888888-0000-0000-0000-000000000000"),
            Guid.Parse("00000000-0000-0000-0000-000000000001")
        ];
        for (var index = 0; index < ids.Length; index++)
        {
            await outbox.EnqueueAsync(new OutgoingEventInfo(ids[index], "order.created", [1, 2, 3], start.AddMilliseconds(index)));
        }

        var claimed = await outbox.GetWaitingEventsAsync(2);

        Assert.Equal(new[] { ids[0], ids[1] }, claimed.Select(e => e.Id).ToArray());
    }

    /// <summary>
    /// 去重键列使用该数据库区分大小写的排序规则
    /// </summary>
    [Fact]
    public async Task InitTables_DedupKeyUsesCaseSensitiveCollation()
    {
        var context = RequireContext();
        var columns = await DatabaseSchemaProbe.GetColumnsAsync(context.Client, context.Client.EntityMaintenance.GetTableName<SysEventInbox>());

        Assert.Equal(ExpectedDedupKeyType, columns["dedup_key"].DataType);
        Assert.Equal(ExpectedDedupKeyCollation, columns["dedup_key"].Collation);
        Assert.Equal(256, columns["dedup_key"].MaxLength);
    }

    /// <summary>
    /// 只有大小写不同的两个消息标识各自入箱，按去重键精确查询与按消息标识判断存在都区分大小写
    /// </summary>
    [Fact]
    public async Task InboxEnqueue_MessageIdsDifferingOnlyByCase_AreBothStored()
    {
        var context = RequireContext();
        var inbox = context.CreateInbox();
        var upper = NewIncomingEvent(context, "Msg-A");
        var lower = NewIncomingEvent(context, "msg-a");

        await inbox.EnqueueAsync(upper);
        await inbox.EnqueueAsync(lower);

        var keys = await context.Client.Queryable<SysEventInbox>().Select(e => e.DedupKey).ToListAsync();
        Assert.Equal(2, keys.Count);
        Assert.Contains("Msg-A", keys);
        Assert.Contains("msg-a", keys);

        var upperRows = await context.Client.Queryable<SysEventInbox>().Where(e => e.DedupKey == "Msg-A").ToListAsync();
        Assert.Equal(upper.Id, Assert.Single(upperRows).BasicId);
        var lowerRows = await context.Client.Queryable<SysEventInbox>().Where(e => e.DedupKey == "msg-a").ToListAsync();
        Assert.Equal(lower.Id, Assert.Single(lowerRows).BasicId);

        Assert.True(await inbox.ExistsByMessageIdAsync("Msg-A"));
        Assert.True(await inbox.ExistsByMessageIdAsync("msg-a"));
        Assert.False(await inbox.ExistsByMessageIdAsync("MSG-A"));
    }

    /// <summary>
    /// 两个不同的非拉丁字符消息标识各自入箱，内容原样保存
    /// </summary>
    [Fact]
    public async Task InboxEnqueue_NonLatinMessageIds_AreBothStored()
    {
        var context = RequireContext();
        var inbox = context.CreateInbox();

        await inbox.EnqueueAsync(NewIncomingEvent(context, "消息-1"));
        await inbox.EnqueueAsync(NewIncomingEvent(context, "訊息-1"));

        var keys = await context.Client.Queryable<SysEventInbox>().Select(e => e.DedupKey).ToListAsync();
        Assert.Equal(2, keys.Count);
        Assert.Contains("消息-1", keys);
        Assert.Contains("訊息-1", keys);
        Assert.True(await inbox.ExistsByMessageIdAsync("消息-1"));
        Assert.True(await inbox.ExistsByMessageIdAsync("訊息-1"));
    }

    /// <summary>
    /// 只有重音不同的两个消息标识各自入箱
    /// </summary>
    [Fact]
    public async Task InboxEnqueue_MessageIdsDifferingOnlyByAccent_AreBothStored()
    {
        var context = RequireContext();
        var inbox = context.CreateInbox();

        await inbox.EnqueueAsync(NewIncomingEvent(context, "msg-é"));
        await inbox.EnqueueAsync(NewIncomingEvent(context, "msg-e"));

        var keys = await context.Client.Queryable<SysEventInbox>().Select(e => e.DedupKey).ToListAsync();
        Assert.Equal(2, keys.Count);
        Assert.Contains("msg-é", keys);
        Assert.Contains("msg-e", keys);
    }

    /// <summary>
    /// 消息标识前后带空白字符时拒绝入箱，不写入任何记录
    /// </summary>
    [Theory]
    [InlineData(" msg-ws")]
    [InlineData("msg-ws ")]
    [InlineData("\tmsg-ws")]
    public async Task InboxEnqueue_MessageIdWithSurroundingWhitespace_Throws(string messageId)
    {
        var context = RequireContext();
        var inbox = context.CreateInbox();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => inbox.EnqueueAsync(NewIncomingEvent(context, messageId)));

        Assert.Equal("incomingEvent", exception.ParamName);
        Assert.Equal(0, await context.Client.Queryable<SysEventInbox>().CountAsync());
    }

    /// <summary>
    /// 已入箱的消息标识加上前后空白字符后不视为已入箱
    /// </summary>
    [Theory]
    [InlineData(" msg-ws")]
    [InlineData("msg-ws ")]
    [InlineData("\tmsg-ws")]
    public async Task InboxExists_MessageIdWithSurroundingWhitespace_ReturnsFalse(string messageId)
    {
        var context = RequireContext();
        var inbox = context.CreateInbox();
        await inbox.EnqueueAsync(NewIncomingEvent(context, "msg-ws"));

        Assert.True(await inbox.ExistsByMessageIdAsync("msg-ws"));
        Assert.False(await inbox.ExistsByMessageIdAsync(messageId));
    }

    /// <summary>
    /// 多个发件箱实例经各自连接同时领取，每条事件只被领取一次且没有异常
    /// </summary>
    [Fact]
    public async Task OutboxClaim_ConcurrentClaimers_NoDuplicateAndNoError()
    {
        var context = RequireContext();
        var connectionString = RequireConnectionString();
        var seeder = context.CreateOutbox();
        var expected = new HashSet<Guid>();
        for (var index = 0; index < ConcurrentEventCount; index++)
        {
            var info = NewOutgoingEvent(context);
            await seeder.EnqueueAsync(info);
            expected.Add(info.Id);
        }

        var claimed = await RunConcurrentClaimersAsync(connectionString, (client, tenant) =>
        {
            var outbox = new SaasEventOutbox(new TestClientResolver(client, tenant), tenant, Options.Create(new SaasEventBoxOptions()), TimeProvider.System);
            return async () => [.. (await outbox.GetWaitingEventsAsync(ConcurrentBatchSize)).Select(item => item.Id)];
        });

        List<OutgoingEventInfo> batch;
        while ((batch = await seeder.GetWaitingEventsAsync(ConcurrentBatchSize)).Count > 0)
        {
            claimed.AddRange(batch.Select(item => item.Id));
        }

        Assert.Equal(claimed.Count, claimed.Distinct().Count());
        Assert.True(expected.SetEquals(claimed), $"[{DatabaseType}] 领取到 {claimed.Distinct().Count()} 条，应为 {expected.Count} 条");
    }

    /// <summary>
    /// 多个收件箱实例经各自连接同时领取，每条事件只被领取一次且没有异常
    /// </summary>
    [Fact]
    public async Task InboxClaim_ConcurrentClaimers_NoDuplicateAndNoError()
    {
        var context = RequireContext();
        var connectionString = RequireConnectionString();
        var seeder = context.CreateInbox();
        var expected = new HashSet<Guid>();
        for (var index = 0; index < ConcurrentEventCount; index++)
        {
            var info = NewIncomingEvent(context, $"db-concurrent-{index}");
            await seeder.EnqueueAsync(info);
            expected.Add(info.Id);
        }

        var claimed = await RunConcurrentClaimersAsync(connectionString, (client, tenant) =>
        {
            var inbox = new SaasEventInbox(
                new TestClientResolver(client, tenant),
                tenant,
                Options.Create(new SaasEventBoxOptions()),
                NullLogger<SaasEventInbox>.Instance,
                TimeProvider.System);
            return async () => [.. (await inbox.GetWaitingEventsAsync(ConcurrentBatchSize)).Select(item => item.Id)];
        });

        List<IncomingEventInfo> batch;
        while ((batch = await seeder.GetWaitingEventsAsync(ConcurrentBatchSize)).Count > 0)
        {
            claimed.AddRange(batch.Select(item => item.Id));
        }

        Assert.Equal(claimed.Count, claimed.Distinct().Count());
        Assert.True(expected.SetEquals(claimed), $"[{DatabaseType}] 领取到 {claimed.Distinct().Count()} 条，应为 {expected.Count} 条");
    }

    /// <summary>
    /// 删除本实例建立的收发件箱表并释放上下文
    /// </summary>
    public void Dispose()
    {
        if (_context is not null)
        {
            DropEventTables(_context.Client);
            _context.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 判断异常是否为该数据库的唯一约束冲突
    /// </summary>
    /// <param name="exception">捕获的异常</param>
    /// <returns>是唯一约束冲突时为 true</returns>
    protected abstract bool IsDuplicateKeyViolation(Exception exception);

    private EventBoxTestContext RequireContext()
    {
        Assert.SkipWhen(_context is null, $"未设置 {ConnectionStringVariable}");
        return _context;
    }

    /// <summary>
    /// 获取连接串，未设置时跳过当前用例
    /// </summary>
    /// <returns>连接串</returns>
    protected string RequireConnectionString()
    {
        Assert.SkipWhen(_connectionString is null, $"未设置 {ConnectionStringVariable}");
        return _connectionString;
    }

    private static DateTimeOffset[] NonUtcOffsetTimes()
    {
        var instant = new DateTimeOffset(2026, 10, 4, 5, 6, 7, 123, TimeSpan.Zero);
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(instant.UtcDateTime);
        return [instant.ToOffset(TimeSpan.FromHours(5)), instant.AddMinutes(1).ToOffset(localOffset)];
    }

    private static async Task<List<SysEventOutbox>> LoadOutboxRowsAsync(EventBoxTestContext context, IReadOnlyList<SysEventOutbox> rows)
    {
        var ids = rows.Select(row => row.BasicId).ToList();
        var stored = await context.Client.Queryable<SysEventOutbox>().Where(e => ids.Contains(e.BasicId)).ToListAsync();
        return [.. ids.Select(id => stored.Single(row => row.BasicId == id))];
    }

    private void AssertSameTime(DateTimeOffset expected, DateTimeOffset actual)
    {
        Assert.Equal(expected.UtcDateTime, actual.UtcDateTime);
        if (PreservesOffset)
        {
            Assert.Equal(expected.Offset, actual.Offset);
        }
    }

    private static SysEventOutbox NewOutboxRow(DateTimeOffset createdTime)
    {
        return new SysEventOutbox(Guid.NewGuid())
        {
            EventName = "order.created",
            EventData = [1, 2, 3],
            CreatedTime = createdTime,
            Status = SysEventOutbox.StatusPending
        };
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

    private async Task<List<Guid>> RunConcurrentClaimersAsync(
        string connectionString,
        Func<ISqlSugarClient, FakeCurrentTenant, Func<Task<List<Guid>>>> createClaimer)
    {
        var clients = Enumerable.Range(0, ConcurrentClaimerCount)
            .Select(_ => IntegrationDatabase.CreateScope(DatabaseType, connectionString))
            .ToList();
        try
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = clients
                .Select(client =>
                {
                    var claim = createClaimer(client, new FakeCurrentTenant());
                    return Task.Run(async () =>
                    {
                        await start.Task;
                        var ids = new List<Guid>();
                        for (var round = 0; round < ConcurrentEventCount; round++)
                        {
                            var batch = await claim();
                            if (batch.Count == 0)
                            {
                                break;
                            }

                            ids.AddRange(batch);
                        }

                        return ids;
                    });
                })
                .ToList();

            start.SetResult();
            var results = await Task.WhenAll(tasks);
            return [.. results.SelectMany(ids => ids)];
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    private static DatabaseIndex RequireIndex(IReadOnlyDictionary<string, DatabaseIndex> indexes, string name)
    {
        var key = name.ToLowerInvariant();
        Assert.True(indexes.TryGetValue(key, out var index), $"缺少索引 {key}");
        return index;
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
}

/// <summary>
/// 收发件箱在 PostgreSQL 上的集成测试，未设置 XIHAN_TEST_POSTGRES 时跳过
/// </summary>
public sealed class EventBoxPostgresTests : EventBoxDatabaseTests
{
    /// <summary>
    /// 构造函数
    /// </summary>
    public EventBoxPostgresTests()
        : base(IntegrationDatabase.PostgresVariable, DbType.PostgreSQL)
    {
    }

    /// <inheritdoc />
    protected override string ExpectedTimestampType
    {
        get
        {
            return "timestamp with time zone";
        }
    }

    /// <inheritdoc />
    protected override string ExpectedBinaryType
    {
        get
        {
            return "bytea";
        }
    }

    /// <inheritdoc />
    protected override long? ExpectedBinaryMaxLength
    {
        get
        {
            return null;
        }
    }

    /// <inheritdoc />
    protected override string? ExpectedDedupKeyCollation
    {
        get
        {
            return null;
        }
    }

    /// <inheritdoc />
    protected override string ExpectedDedupKeyType
    {
        get
        {
            return "character varying";
        }
    }

    /// <inheritdoc />
    protected override bool PreservesOffset
    {
        get
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override bool IsDuplicateKeyViolation(Exception exception)
    {
        return IntegrationDatabase.FindException<PostgresException>(exception) is { SqlState: "23505" };
    }
}

/// <summary>
/// 收发件箱在 SQL Server 上的集成测试，未设置 XIHAN_TEST_SQLSERVER 时跳过
/// </summary>
public sealed partial class EventBoxSqlServerTests : EventBoxDatabaseTests
{
    /// <summary>
    /// 构造函数
    /// </summary>
    public EventBoxSqlServerTests()
        : base(IntegrationDatabase.SqlServerVariable, DbType.SqlServer)
    {
    }

    /// <inheritdoc />
    protected override string ExpectedTimestampType
    {
        get
        {
            return "datetimeoffset";
        }
    }

    /// <inheritdoc />
    protected override string ExpectedBinaryType
    {
        get
        {
            return "varbinary";
        }
    }

    /// <inheritdoc />
    protected override long? ExpectedBinaryMaxLength
    {
        get
        {
            return -1;
        }
    }

    /// <inheritdoc />
    protected override string? ExpectedDedupKeyCollation
    {
        get
        {
            return ResolveExpectedDedupKeyCollation(RequireConnectionString());
        }
    }

    /// <inheritdoc />
    protected override string ExpectedDedupKeyType
    {
        get
        {
            return "nvarchar";
        }
    }

    /// <inheritdoc />
    protected override bool PreservesOffset
    {
        get
        {
            return true;
        }
    }

    /// <inheritdoc />
    protected override bool IsDuplicateKeyViolation(Exception exception)
    {
        return IntegrationDatabase.FindException<SqlException>(exception) is { Number: 2601 or 2627 };
    }
}

/// <summary>
/// 收发件箱在 MySQL 上的集成测试，未设置 XIHAN_TEST_MYSQL 时跳过
/// </summary>
public sealed class EventBoxMySqlTests : EventBoxDatabaseTests
{
    /// <summary>
    /// 构造函数
    /// </summary>
    public EventBoxMySqlTests()
        : base(IntegrationDatabase.MySqlVariable, DbType.MySql)
    {
    }

    /// <inheritdoc />
    protected override string ExpectedTimestampType
    {
        get
        {
            return "datetime";
        }
    }

    /// <inheritdoc />
    protected override string ExpectedBinaryType
    {
        get
        {
            return "longblob";
        }
    }

    /// <inheritdoc />
    protected override long? ExpectedBinaryMaxLength
    {
        get
        {
            return 4294967295L;
        }
    }

    /// <inheritdoc />
    protected override string? ExpectedDedupKeyCollation
    {
        get
        {
            return "utf8mb4_bin";
        }
    }

    /// <inheritdoc />
    protected override string ExpectedDedupKeyType
    {
        get
        {
            return "varchar";
        }
    }

    /// <inheritdoc />
    protected override bool PreservesOffset
    {
        get
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override bool IsDuplicateKeyViolation(Exception exception)
    {
        return IntegrationDatabase.FindException<MySqlException>(exception) is { Number: 1062 };
    }
}
