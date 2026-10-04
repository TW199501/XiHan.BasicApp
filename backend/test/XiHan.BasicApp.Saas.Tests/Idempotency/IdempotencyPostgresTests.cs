// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using DataRow = System.Data.DataRow;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.Idempotency;
using XiHan.BasicApp.Web.Core.Idempotency;
using XiHan.Framework.Uow;

namespace XiHan.BasicApp.Saas.Tests.Idempotency;

/// <summary>
/// 接口幂等存储在 PostgreSQL 上的集成测试，未设置 XIHAN_TEST_POSTGRES 时跳过
/// </summary>
/// <remarks>
/// 每个测试实例重建幂等记录表，结束时只删除这张表。
/// </remarks>
public sealed class IdempotencyPostgresTests : IDisposable
{
    private const string ConnectionStringVariable = "XIHAN_TEST_POSTGRES";

    private readonly string? _connectionString;
    private readonly SqlSugarClient? _client;
    private readonly ServiceProvider _provider = IdempotencyStoreTestContext.BuildUnitOfWorkProvider();
    private readonly IdempotencyOptions _options = new();
    private readonly ManualTimeProvider _clock = new();

    /// <summary>
    /// 设置了连接串时重建幂等记录表
    /// </summary>
    public IdempotencyPostgresTests()
    {
        _connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        _client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connectionString,
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true
        });
        DropIdempotencyTable(_client);
        _client.CodeFirst.InitTables(typeof(SysIdempotencyRecord));
    }

    /// <summary>
    /// 时间列为 timestamp with time zone，响应快照列为 bytea，记录键摘要上有唯一索引
    /// </summary>
    [Fact]
    public async Task InitTables_CreatesExpectedColumnTypesAndUniqueIndex()
    {
        var client = RequireClient();
        var tableName = client.EntityMaintenance.GetTableName<SysIdempotencyRecord>();

        var columns = await GetColumnsAsync(client, tableName);

        Assert.Equal("Sys_Idempotency_Record", tableName);
        Assert.Equal("timestamp with time zone", columns["lease_expires_time"].DataType);
        Assert.Equal("timestamp with time zone", columns["expires_time"].DataType);
        Assert.Equal("timestamp with time zone", columns["created_time"].DataType);
        Assert.Equal("timestamp with time zone", columns["completed_time"].DataType);
        Assert.Equal("bytea", columns["response_body"].DataType);
        Assert.Equal(64, columns["key_hash"].MaxLength);
        Assert.Equal(128, columns["idempotency_key"].MaxLength);
        Assert.Equal(512, columns["endpoint"].MaxLength);

        var indexes = await GetIndexDefinitionsAsync(client, tableName);
        var uniqueIndexName = $"ux_{tableName}_keha".ToLowerInvariant();
        Assert.True(indexes.TryGetValue(uniqueIndexName, out var definition), $"缺少索引 {uniqueIndexName}");
        Assert.Contains("UNIQUE", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("key_hash", definition, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 完成写入随业务事务回滚，记录仍为处理中且没有快照
    /// </summary>
    [Fact]
    public async Task Completion_RollsBackWithBusinessTransaction()
    {
        var client = RequireClient();
        var store = CreateStore(client, _clock);
        var key = CreateKey("rollback");
        var acquired = await store.TryAcquireAsync(key, "fp-a", isTransactional: true);
        Assert.Equal(IdempotencyAcquireStatus.Acquired, acquired.Status);

        client.Ado.BeginTran();
        try
        {
            await store.CompleteAsync(key, acquired.OwnerToken, new StoredResponse(200, [9, 9]));
        }
        finally
        {
            client.Ado.RollbackTran();
        }

        var record = FindRecord(client, key);
        Assert.NotNull(record);
        Assert.Equal(SysIdempotencyRecord.StatusProcessing, record.Status);
        Assert.NotEqual(SysIdempotencyRecord.StatusCompleted, record.Status);
        Assert.Null(record.ResponseBody);
        Assert.Equal(IdempotencyAcquireStatus.InProgress, (await store.TryAcquireAsync(key, "fp-a", isTransactional: true)).Status);
    }

    /// <summary>
    /// 完成写入随业务事务提交，之后重播状态码与快照
    /// </summary>
    [Fact]
    public async Task Completion_CommitsWithBusinessTransaction()
    {
        var client = RequireClient();
        var store = CreateStore(client, _clock);
        var key = CreateKey("commit");
        var body = new byte[] { 7, 8, 9 };
        var acquired = await store.TryAcquireAsync(key, "fp-a", isTransactional: true);

        client.Ado.BeginTran();
        await store.CompleteAsync(key, acquired.OwnerToken, new StoredResponse(202, body));
        client.Ado.CommitTran();

        var replay = await store.TryAcquireAsync(key, "fp-a", isTransactional: true);
        Assert.Equal(IdempotencyAcquireStatus.Replay, replay.Status);
        Assert.Equal(202, replay.Response!.StatusCode);
        Assert.Equal(body, replay.Response.Body);
    }

    /// <summary>
    /// 无响应快照的完成可写入，之后重播空快照
    /// </summary>
    [Fact]
    public async Task Completion_WithNullBody_ReplaysNullBody()
    {
        var client = RequireClient();
        var store = CreateStore(client, _clock);
        var key = CreateKey("null-body");
        var acquired = await store.TryAcquireAsync(key, "fp-a", isTransactional: true);

        await store.CompleteAsync(key, acquired.OwnerToken, new StoredResponse(204, null));

        var replay = await store.TryAcquireAsync(key, "fp-a", isTransactional: true);
        Assert.Equal(IdempotencyAcquireStatus.Replay, replay.Status);
        Assert.Equal(204, replay.Response!.StatusCode);
        Assert.Null(replay.Response.Body);
    }

    /// <summary>
    /// 两个存储实例同时取得同一键，恰好一个取得成功
    /// </summary>
    [Fact]
    public async Task ConcurrentAcquire_TwoStores_ExactlyOneAcquired()
    {
        var connectionString = RequireConnectionString();
        using var firstClient = CreateScopeClient(connectionString);
        using var secondClient = CreateScopeClient(connectionString);
        var first = CreateStore(firstClient, TimeProvider.System);
        var second = CreateStore(secondClient, TimeProvider.System);
        var key = CreateKey("pair");

        var results = await Task.WhenAll(
            Task.Run(() => first.TryAcquireAsync(key, "fp-a", isTransactional: true)),
            Task.Run(() => second.TryAcquireAsync(key, "fp-a", isTransactional: true)));

        Assert.Single(results, result => result.Status == IdempotencyAcquireStatus.Acquired);
        Assert.Single(results, result => result.Status == IdempotencyAcquireStatus.InProgress);
        Assert.Equal(1, await RequireClient().Queryable<SysIdempotencyRecord>().CountAsync());
    }

    /// <summary>
    /// 两个客户端的 50 个并发请求抢同一个键，只有一个取得
    /// </summary>
    [Fact]
    public async Task ConcurrentAcquire_FiftyRequestsAcrossTwoClients_ExactlyOneAcquired()
    {
        var connectionString = RequireConnectionString();
        using var firstClient = CreateScopeClient(connectionString);
        using var secondClient = CreateScopeClient(connectionString);
        var stores = new[]
        {
            CreateStore(firstClient, TimeProvider.System),
            CreateStore(secondClient, TimeProvider.System)
        };
        var key = CreateKey("fifty");
        using var gate = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 50)
            .Select(index => Task.Factory.StartNew(() =>
            {
                gate.Wait();
                return stores[index % 2].TryAcquireAsync(key, "fp-a", isTransactional: true).GetAwaiter().GetResult();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result.Status == IdempotencyAcquireStatus.Acquired);
        Assert.Equal(49, results.Count(result => result.Status == IdempotencyAcquireStatus.InProgress));
        Assert.Equal(1, await RequireClient().Queryable<SysIdempotencyRecord>().CountAsync());
    }

    /// <summary>
    /// 事务型处理中记录在租约到期前为处理中，到期后可被重新取得
    /// </summary>
    [Fact]
    public async Task ProcessingRecord_CanBeReacquiredAfterLeaseExpires()
    {
        var client = RequireClient();
        var store = CreateStore(client, _clock);
        var key = CreateKey("lease");
        var first = await store.TryAcquireAsync(key, "fp-a", isTransactional: true);

        _clock.Advance(_options.ProcessingLease - TimeSpan.FromSeconds(1));
        Assert.Equal(IdempotencyAcquireStatus.InProgress, (await store.TryAcquireAsync(key, "fp-a", isTransactional: true)).Status);

        _clock.Advance(TimeSpan.FromSeconds(2));
        var takeover = await store.TryAcquireAsync(key, "fp-a", isTransactional: true);

        Assert.Equal(IdempotencyAcquireStatus.Acquired, takeover.Status);
        Assert.NotEqual(first.OwnerToken, takeover.OwnerToken);
        Assert.Equal(takeover.OwnerToken, FindRecord(client, key)!.OwnerToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CompleteAsync(key, first.OwnerToken, new StoredResponse(200, null)));
    }

    /// <summary>
    /// 删除本实例建立的幂等记录表并释放资源
    /// </summary>
    public void Dispose()
    {
        if (_client is not null)
        {
            DropIdempotencyTable(_client);
            _client.Dispose();
        }

        _provider.Dispose();
    }

    private static void DropIdempotencyTable(ISqlSugarClient client)
    {
        var tableName = client.EntityMaintenance.GetTableName<SysIdempotencyRecord>();
        if (client.DbMaintenance.IsAnyTable(tableName, false))
        {
            client.DbMaintenance.DropTable(tableName);
        }
    }

    private static SqlSugarScope CreateScopeClient(string connectionString)
    {
        return new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true
        });
    }

    private static IdempotencyRecordKey CreateKey(string key)
    {
        return new IdempotencyRecordKey(string.Empty, "42", "POST", "/api/orders", $"pg-{key}-{Guid.NewGuid():N}");
    }

    private static SysIdempotencyRecord? FindRecord(ISqlSugarClient client, IdempotencyRecordKey key)
    {
        var keyHash = key.ComputeHash();
        return client.Queryable<SysIdempotencyRecord>().Where(record => record.KeyHash == keyHash).First();
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

    private static async Task<Dictionary<string, string>> GetIndexDefinitionsAsync(ISqlSugarClient client, string tableName)
    {
        var table = await client.Ado.GetDataTableAsync(
            "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = current_schema() AND lower(tablename) = lower(@tableName)",
            new { tableName });

        return table.Rows.Cast<DataRow>()
            .ToDictionary(row => ((string)row["indexname"]).ToLowerInvariant(), row => (string)row["indexdef"]);
    }

    private SaasIdempotencyStore CreateStore(ISqlSugarClient client, TimeProvider timeProvider)
    {
        return new SaasIdempotencyStore(
            new StubClientResolver(client),
            _provider.GetRequiredService<IUnitOfWorkManager>(),
            Microsoft.Extensions.Options.Options.Create(_options),
            timeProvider);
    }

    private SqlSugarClient RequireClient()
    {
        Assert.SkipWhen(_client is null, $"未设置 {ConnectionStringVariable}");
        return _client;
    }

    private string RequireConnectionString()
    {
        Assert.SkipWhen(_client is null || _connectionString is null, $"未设置 {ConnectionStringVariable}");
        return _connectionString;
    }
}
