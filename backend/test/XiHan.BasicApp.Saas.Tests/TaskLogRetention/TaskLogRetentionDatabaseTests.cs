// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using XiHan.BasicApp.Saas.Application.Services;
using XiHan.BasicApp.Saas.Domain.Configurations;
using XiHan.BasicApp.Saas.Domain.DomainServices;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.Tasks;
using XiHan.BasicApp.Saas.Tests.TestDatabases;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Domain.Entities.Abstracts;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.BasicApp.Saas.Tests.TaskLogRetention;

/// <summary>
/// 任务执行历史清理在真实数据库上的集成测试基类，未设置对应连接串环境变量时跳过
/// </summary>
/// <remarks>
/// <para>测试库按框架 ApplySugarGlobalFilters 的口径注册租户全局过滤器，并开启删除自动带过滤。</para>
/// <para>每个测试实例只删除自己新建的 Sys_Task_Log 月表；库中已有 Sys_Task_Log 月表时拒绝运行。</para>
/// <para>除任务执行历史外的日志实体解析到空的 SQLite 库，不触碰真实库中的其它表。</para>
/// </remarks>
public abstract class TaskLogRetentionDatabaseTests : IDisposable
{
    private const string TablePrefix = "Sys_Task_Log_";

    private const long IsolatedTenantId = 7;

    private const long UnvisitedTenantId = 9;

    private readonly SqlSugarClient? _client;

    private readonly HashSet<string> _createdTables = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _deleteStatements = [];

    private readonly TaskLogTestDatabase _otherLogs = new();

    private readonly ITestOutputHelper _output;

    private long _nextId = 1;

    /// <summary>
    /// 设置了连接串时连接真实库并注册租户过滤
    /// </summary>
    /// <param name="output">测试输出</param>
    /// <param name="connectionStringVariable">连接串环境变量名</param>
    /// <param name="databaseType">数据库类型</param>
    protected TaskLogRetentionDatabaseTests(ITestOutputHelper output, string connectionStringVariable, DbType databaseType)
    {
        _output = output;
        ConnectionStringVariable = connectionStringVariable;
        DatabaseType = databaseType;
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        _client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DatabaseType,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings
            {
                IsAutoDeleteQueryFilter = true,
                IsAutoUpdateQueryFilter = true
            }
        });
        _client.QueryFilter.AddTableFilter<IMultiTenantEntity>(entity => entity.TenantId == 0 || entity.TenantId == ScopeTenantId);
        _client.QueryFilter.AddTableFilter<IStrictMultiTenantEntity>(entity => entity.TenantId == ScopeTenantId);
        _client.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                _deleteStatements.Add(sql.Trim());
            }
        };

        var existing = ListTaskLogTables();
        if (existing.Count > 0)
        {
            throw new InvalidOperationException($"测试库中已有 {existing.Count} 张 {TablePrefix} 月表，拒绝运行以免删除非测试数据。");
        }
    }

    /// <summary>
    /// 连接串环境变量名
    /// </summary>
    protected string ConnectionStringVariable { get; }

    /// <summary>
    /// 数据库类型
    /// </summary>
    protected DbType DatabaseType { get; }

    /// <summary>
    /// 租户过滤使用的作用域租户；平台为 0
    /// </summary>
    private long ScopeTenantId { get; set; }

    /// <summary>
    /// 日志保留清理按作用域删除平台与库隔离租户的过期任务历史，跨 3 个月表，未访问的租户与未过期行保留
    /// </summary>
    [Fact]
    public async Task LogRetentionCleanup_DeletesExpiredRowsPerScopeAcrossMonthTables()
    {
        var client = RequireClient();
        var now = DateTimeOffset.UtcNow;
        Insert(client, now.AddDays(-250), 0);
        Insert(client, now.AddDays(-200), 0);
        Insert(client, now.AddDays(-1), 0);
        Insert(client, now.AddDays(-250), IsolatedTenantId);
        Insert(client, now.AddDays(-200), IsolatedTenantId);
        Insert(client, now.AddDays(-1), IsolatedTenantId);
        Insert(client, now.AddDays(-250), UnvisitedTenantId);
        Assert.True(_createdTables.Count >= 3);

        var summary = await CreateCleanupTask(client, [null, IsolatedTenantId]).ExecuteAsync();

        WriteEvidence(summary);
        Assert.DoesNotContain("失败", summary, StringComparison.Ordinal);
        Assert.Contains("共删除 4 行", summary, StringComparison.Ordinal);
        var remaining = AllRows(client);
        Assert.Equal(3, remaining.Count);
        Assert.Contains(remaining, row => row.TenantId == 0 && row.CreatedTime > now.AddDays(-2));
        Assert.Contains(remaining, row => row.TenantId == IsolatedTenantId && row.CreatedTime > now.AddDays(-2));
        Assert.Contains(remaining, row => row.TenantId == UnvisitedTenantId);
    }

    /// <summary>
    /// 任务存储清理只删除当前租户上下文可见的过期行：平台上下文删平台行，租户上下文删该租户行
    /// </summary>
    [Fact]
    public async Task JobStoreCleanupHistory_DeletesExpiredRowsOfAmbientTenantOnly()
    {
        var client = RequireClient();
        var now = DateTimeOffset.UtcNow;
        Insert(client, now.AddDays(-90), 0);
        Insert(client, now.AddDays(-45), 0);
        Insert(client, now.AddDays(-5), 0);
        Insert(client, now.AddDays(-90), IsolatedTenantId);
        Insert(client, now.AddDays(-5), IsolatedTenantId);
        Assert.True(_createdTables.Count >= 3);
        var store = CreateJobStore(client);

        ScopeTenantId = 0;
        await store.CleanupHistoryAsync(30);

        var afterPlatform = AllRows(client);
        Assert.Equal(3, afterPlatform.Count);
        Assert.Single(afterPlatform, row => row.TenantId == 0);
        Assert.Equal(2, afterPlatform.Count(row => row.TenantId == IsolatedTenantId));

        ScopeTenantId = IsolatedTenantId;
        await store.CleanupHistoryAsync(30);
        ScopeTenantId = 0;

        WriteEvidence("任务存储清理完成");
        var remaining = AllRows(client);
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, row => Assert.True(row.CreatedTime > now.AddDays(-6)));
        Assert.Contains(remaining, row => row.TenantId == 0);
        Assert.Contains(remaining, row => row.TenantId == IsolatedTenantId);
    }

    /// <summary>
    /// 删除本测试新建的 Sys_Task_Log 月表
    /// </summary>
    public void Dispose()
    {
        if (_client is not null)
        {
            _client.Aop.OnLogExecuting = null;
            foreach (var table in ListTaskLogTables().Where(_createdTables.Contains))
            {
                _client.DbMaintenance.DropTable(table);
            }

            _client.Dispose();
        }

        _otherLogs.Dispose();
        GC.SuppressFinalize(this);
    }

    private SqlSugarClient RequireClient()
    {
        Assert.SkipWhen(_client is null, $"未设置 {ConnectionStringVariable}");
        return _client;
    }

    private void Insert(SqlSugarClient client, DateTimeOffset createdTime, long tenantId)
    {
        var log = new SysTaskLog
        {
            TenantId = tenantId,
            TaskId = 1,
            TaskCode = "test-task",
            TaskName = "测试任务",
            StartTime = createdTime,
            CreatedTime = createdTime
        };
        typeof(SysTaskLog).GetProperty(nameof(SysTaskLog.BasicId))!.SetValue(log, _nextId++);
        client.Insertable(log).SplitTable().ExecuteCommand();
        _createdTables.Add(client.SplitHelper<SysTaskLog>().GetTableName(createdTime.UtcDateTime));
    }

    private static List<(long TenantId, DateTimeOffset CreatedTime)> AllRows(SqlSugarClient client)
    {
        return client.Queryable<SysTaskLog>().ClearFilter().SplitTable().ToList()
            .Select(log => (log.TenantId, log.CreatedTime))
            .ToList();
    }

    private List<string> ListTaskLogTables()
    {
        return _client!.DbMaintenance.GetTableInfoList(false)
            .Select(table => table.Name)
            .Where(name => name.StartsWith(TablePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void WriteEvidence(string summary)
    {
        _output.WriteLine($"[{DatabaseType}] {summary}");
        _output.WriteLine($"[{DatabaseType}] 新建月表：{string.Join(", ", _createdTables.Order(StringComparer.OrdinalIgnoreCase))}");
        foreach (var statement in _deleteStatements)
        {
            _output.WriteLine($"[{DatabaseType}] {statement}");
        }
    }

    private LogRetentionCleanupTask CreateCleanupTask(SqlSugarClient client, long?[] scopes)
    {
        var resolver = new Mock<ISqlSugarClientResolver> { CallBase = true };
        resolver.Setup(value => value.GetCurrentClient()).Returns(_otherLogs.Client);
        resolver.Setup(value => value.GetClientForEntity(It.IsAny<Type>())).Returns(_otherLogs.Client);
        resolver.Setup(value => value.GetClientForEntity(typeof(SysTaskLog))).Returns(client);

        var configuration = new Mock<ISaasConfigurationService>();
        configuration
            .Setup(value => value.GetJsonAsync(SaasConfigKeys.Log.RetentionDays, LogRetentionCleanupTask.DefaultRetentionDays, It.IsAny<CancellationToken>()))
            .ReturnsAsync(180);

        var scopeRunner = new Mock<ITenantDataScopeRunner>();
        scopeRunner
            .Setup(value => value.RunAsync(It.IsAny<Func<long?, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<long?, Task>, CancellationToken>(async (action, _) =>
            {
                foreach (var scope in scopes)
                {
                    ScopeTenantId = scope ?? 0;
                    await action(scope);
                }

                ScopeTenantId = 0;
            });

        var currentTenant = new Mock<ICurrentTenant>();
        currentTenant.Setup(value => value.Change(It.IsAny<long?>(), It.IsAny<string?>())).Returns(Mock.Of<IDisposable>());

        return new LogRetentionCleanupTask(resolver.Object, configuration.Object, scopeRunner.Object, currentTenant.Object, NullLogger<LogRetentionCleanupTask>.Instance);
    }

    private static SaasJobStore CreateJobStore(SqlSugarClient client)
    {
        var resolver = new Mock<ISqlSugarClientResolver> { CallBase = true };
        resolver.Setup(value => value.GetClientForEntity(typeof(SysTaskLog))).Returns(client);

        var services = new ServiceCollection();
        services.AddSingleton(resolver.Object);
        var provider = services.BuildServiceProvider();

        return new SaasJobStore(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SaasJobStore>.Instance);
    }
}

/// <summary>
/// 任务执行历史清理在 PostgreSQL 上的集成测试，未设置 XIHAN_TEST_POSTGRES 时跳过
/// </summary>
public sealed class TaskLogRetentionPostgresTests : TaskLogRetentionDatabaseTests
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="output">测试输出</param>
    public TaskLogRetentionPostgresTests(ITestOutputHelper output)
        : base(output, IntegrationDatabase.PostgresVariable, DbType.PostgreSQL)
    {
    }
}

/// <summary>
/// 任务执行历史清理在 MySQL 上的集成测试，未设置 XIHAN_TEST_MYSQL 时跳过
/// </summary>
public sealed class TaskLogRetentionMySqlTests : TaskLogRetentionDatabaseTests
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="output">测试输出</param>
    public TaskLogRetentionMySqlTests(ITestOutputHelper output)
        : base(output, IntegrationDatabase.MySqlVariable, DbType.MySql)
    {
    }
}

/// <summary>
/// 任务执行历史清理在 SQL Server 上的集成测试，未设置 XIHAN_TEST_SQLSERVER 时跳过
/// </summary>
public sealed class TaskLogRetentionSqlServerTests : TaskLogRetentionDatabaseTests
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="output">测试输出</param>
    public TaskLogRetentionSqlServerTests(ITestOutputHelper output)
        : base(output, IntegrationDatabase.SqlServerVariable, DbType.SqlServer)
    {
    }
}
