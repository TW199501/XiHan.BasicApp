// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.Idempotency;
using XiHan.BasicApp.Saas.Tests.TestDatabases;
using XiHan.BasicApp.Web.Core.Idempotency;
using XiHan.Framework.Uow;

namespace XiHan.BasicApp.Saas.Tests.Idempotency;

/// <summary>
/// 接口幂等存储在真实数据库上的集成测试基类，未设置对应连接串环境变量时跳过
/// </summary>
/// <remarks>
/// 每个测试实例重建幂等记录表，结束时只删除这张表。
/// </remarks>
public abstract class IdempotencyDatabaseTests : IDisposable
{
    private readonly string? _connectionString;
    private readonly SqlSugarClient? _client;
    private readonly ServiceProvider _provider = IdempotencyStoreTestContext.BuildUnitOfWorkProvider();

    /// <summary>
    /// 设置了连接串时重建幂等记录表
    /// </summary>
    /// <param name="connectionStringVariable">连接串环境变量名</param>
    /// <param name="databaseType">数据库种类</param>
    protected IdempotencyDatabaseTests(string connectionStringVariable, DbType databaseType)
    {
        ConnectionStringVariable = connectionStringVariable;
        DatabaseType = databaseType;
        _connectionString = IntegrationDatabase.GetConnectionString(connectionStringVariable);
        if (_connectionString is null)
        {
            return;
        }

        _client = IntegrationDatabase.CreateClient(databaseType, _connectionString);
        DropIdempotencyTable(_client);
        _client.CodeFirst.InitTables(typeof(SysIdempotencyRecord));
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
    /// 幂等配置
    /// </summary>
    protected IdempotencyOptions IdempotencyOptions { get; } = new();

    /// <summary>
    /// 可推进的时钟
    /// </summary>
    protected ManualTimeProvider Clock { get; } = new();

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
    /// 时间列、响应快照列与字符列长度符合该数据库的预期，记录键摘要上有唯一索引
    /// </summary>
    [Fact]
    public async Task InitTables_CreatesExpectedColumnTypesAndUniqueIndex()
    {
        var client = RequireClient();
        var tableName = client.EntityMaintenance.GetTableName<SysIdempotencyRecord>();

        var columns = await DatabaseSchemaProbe.GetColumnsAsync(client, tableName);

        Assert.Equal("Sys_Idempotency_Record", tableName);
        Assert.Equal(ExpectedTimestampType, columns["lease_expires_time"].DataType);
        Assert.Equal(ExpectedTimestampType, columns["expires_time"].DataType);
        Assert.Equal(ExpectedTimestampType, columns["created_time"].DataType);
        Assert.Equal(ExpectedTimestampType, columns["completed_time"].DataType);
        Assert.Equal(ExpectedBinaryType, columns["response_body"].DataType);
        Assert.Equal(ExpectedBinaryMaxLength, columns["response_body"].MaxLength);
        Assert.Equal(64, columns["key_hash"].MaxLength);
        Assert.Equal(128, columns["idempotency_key"].MaxLength);
        Assert.Equal(512, columns["endpoint"].MaxLength);

        var indexes = await DatabaseSchemaProbe.GetIndexesAsync(client, tableName);
        var uniqueIndexName = $"ux_{tableName}_keha".ToLowerInvariant();
        Assert.True(indexes.TryGetValue(uniqueIndexName, out var index), $"缺少索引 {uniqueIndexName}");
        Assert.True(index.IsUnique);
        Assert.Equal(new[] { "key_hash" }, index.Columns);
    }

    /// <summary>
    /// 完成写入随业务事务回滚，记录仍为处理中且没有快照
    /// </summary>
    [Fact]
    public async Task Completion_RollsBackWithBusinessTransaction()
    {
        var client = RequireClient();
        var store = CreateStore(client, Clock);
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
        var store = CreateStore(client, Clock);
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
        var store = CreateStore(client, Clock);
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
        using var firstClient = IntegrationDatabase.CreateScope(DatabaseType, connectionString);
        using var secondClient = IntegrationDatabase.CreateScope(DatabaseType, connectionString);
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
        using var firstClient = IntegrationDatabase.CreateScope(DatabaseType, connectionString);
        using var secondClient = IntegrationDatabase.CreateScope(DatabaseType, connectionString);
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
        var store = CreateStore(client, Clock);
        var key = CreateKey("lease");
        var first = await store.TryAcquireAsync(key, "fp-a", isTransactional: true);

        Clock.Advance(IdempotencyOptions.ProcessingLease - TimeSpan.FromSeconds(1));
        Assert.Equal(IdempotencyAcquireStatus.InProgress, (await store.TryAcquireAsync(key, "fp-a", isTransactional: true)).Status);

        Clock.Advance(TimeSpan.FromSeconds(2));
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
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 取得测试客户端，未设置连接串时跳过
    /// </summary>
    protected SqlSugarClient RequireClient()
    {
        Assert.SkipWhen(_client is null, $"未设置 {ConnectionStringVariable}");
        return _client!;
    }

    /// <summary>
    /// 取得连接串，未设置时跳过
    /// </summary>
    protected string RequireConnectionString()
    {
        Assert.SkipWhen(_connectionString is null, $"未设置 {ConnectionStringVariable}");
        return _connectionString!;
    }

    /// <summary>
    /// 建立使用指定客户端与时钟的存储
    /// </summary>
    protected SaasIdempotencyStore CreateStore(ISqlSugarClient client, TimeProvider timeProvider)
    {
        return new SaasIdempotencyStore(
            new StubClientResolver(client),
            _provider.GetRequiredService<IUnitOfWorkManager>(),
            Microsoft.Extensions.Options.Options.Create(IdempotencyOptions),
            timeProvider);
    }

    /// <summary>
    /// 建立不与其他用例重复的记录键
    /// </summary>
    protected static IdempotencyRecordKey CreateKey(string key)
    {
        return new IdempotencyRecordKey(string.Empty, "42", "POST", "/api/orders", $"db-{key}-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// 按记录键读取记录
    /// </summary>
    protected static SysIdempotencyRecord? FindRecord(ISqlSugarClient client, IdempotencyRecordKey key)
    {
        var keyHash = key.ComputeHash();
        return client.Queryable<SysIdempotencyRecord>().Where(record => record.KeyHash == keyHash).First();
    }

    private static void DropIdempotencyTable(ISqlSugarClient client)
    {
        var tableName = client.EntityMaintenance.GetTableName<SysIdempotencyRecord>();
        if (client.DbMaintenance.IsAnyTable(tableName, false))
        {
            client.DbMaintenance.DropTable(tableName);
        }
    }
}

/// <summary>
/// 接口幂等存储在 PostgreSQL 上的集成测试，未设置 XIHAN_TEST_POSTGRES 时跳过
/// </summary>
public sealed class IdempotencyPostgresTests : IdempotencyDatabaseTests
{
    /// <summary>
    /// 构造函数
    /// </summary>
    public IdempotencyPostgresTests()
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
}

/// <summary>
/// 接口幂等存储在 SQL Server 上的集成测试，未设置 XIHAN_TEST_SQLSERVER 时跳过
/// </summary>
public sealed class IdempotencySqlServerTests : IdempotencyDatabaseTests
{
    /// <summary>
    /// 构造函数
    /// </summary>
    public IdempotencySqlServerTests()
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
}

/// <summary>
/// 接口幂等存储在 MySQL 上的集成测试，未设置 XIHAN_TEST_MYSQL 时跳过
/// </summary>
public sealed class IdempotencyMySqlTests : IdempotencyDatabaseTests
{
    /// <summary>
    /// 构造函数
    /// </summary>
    public IdempotencyMySqlTests()
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
}
