// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using XiHan.BasicApp.Saas.Infrastructure.Upgrade;
using XiHan.Framework.Core.Application;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Upgrade.Abstractions;
using XiHan.Framework.Upgrade.Models;
using XiHan.Framework.Upgrade.Options;
using XiHan.Framework.Upgrade.Services;

namespace XiHan.BasicApp.Saas.Tests.Upgrade;

/// <summary>
/// 升级测试用的工厂方法
/// </summary>
internal static class UpgradeTestDoubles
{
    /// <summary>
    /// 测试中的应用版本
    /// </summary>
    public const string AppVersion = "5.7.0";

    /// <summary>
    /// 当前连接为指定数据库类型的解析器
    /// </summary>
    public static ISqlSugarClientResolver CreateResolver(DbType dbType)
    {
        var client = Mock.Of<ISqlSugarClient>(sugar => sugar.CurrentConnectionConfig == new ConnectionConfig { DbType = dbType });
        return CreateResolver(client);
    }

    /// <summary>
    /// 当前连接为指定客户端的解析器
    /// </summary>
    public static ISqlSugarClientResolver CreateResolver(ISqlSugarClient client)
    {
        return Mock.Of<ISqlSugarClientResolver>(resolver => resolver.GetCurrentClient() == client);
    }

    /// <summary>
    /// 升级选项：固定应用版本与节点名，关闭维护模式与多租户分发
    /// </summary>
    public static IOptions<XiHanUpgradeOptions> CreateOptions(string rootPath)
    {
        return Options.Create(new XiHanUpgradeOptions
        {
            AppVersion = AppVersion,
            NodeName = "test-node",
            MigrationsRootPath = rootPath,
            EnableMaintenanceMode = false,
            EnableMultiTenantIsolation = false
        });
    }

    /// <summary>
    /// 指定根目录与数据库类型的方言提供者
    /// </summary>
    public static DialectAwareUpgradeScriptProvider CreateProvider(string rootPath, DbType dbType)
    {
        return new DialectAwareUpgradeScriptProvider(CreateOptions(rootPath), CreateResolver(dbType));
    }

    /// <summary>
    /// 以方言提供者为唯一脚本来源的真实升级引擎
    /// </summary>
    public static UpgradeEngine CreateEngine(
        string rootPath,
        ISqlSugarClientResolver resolver,
        IUpgradeVersionStore versionStore,
        IUpgradeLockProvider lockProvider,
        IUpgradeMigrationExecutor migrationExecutor)
    {
        var options = CreateOptions(rootPath);
        return new UpgradeEngine(
            versionStore,
            [new DialectAwareUpgradeScriptProvider(options, resolver)],
            lockProvider,
            Mock.Of<IUpgradeMaintenanceModeManager>(),
            Mock.Of<IUpgradeFileUpdater>(),
            Mock.Of<IRollingRestartCoordinator>(),
            Mock.Of<IUpgradeTenantProvider>(),
            migrationExecutor,
            Mock.Of<IServiceProvider>(),
            Mock.Of<IApplicationInfoAccessor>(),
            options,
            NullLogger<UpgradeEngine>.Instance);
    }
}

/// <summary>
/// 内存版本存储：单库一行版本记录与台账
/// </summary>
internal sealed class InMemoryUpgradeVersionStore : IUpgradeVersionStore
{
    /// <summary>
    /// 版本记录；未建立时为 null
    /// </summary>
    public UpgradeVersionState? State { get; private set; }

    /// <summary>
    /// 执行台账
    /// </summary>
    public List<UpgradeMigrationHistory> Histories { get; } = [];

    /// <summary>
    /// 预置一条版本记录
    /// </summary>
    public void Seed(string dbVersion)
    {
        State = new UpgradeVersionState
        {
            Id = 1,
            TenantId = 0,
            AppVersion = UpgradeTestDoubles.AppVersion,
            DbVersion = dbVersion
        };
    }

    public Task EnsureTablesAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<UpgradeVersionState> GetOrCreateAsync(string currentAppVersion, string minSupportVersion, CancellationToken cancellationToken = default)
    {
        State ??= new UpgradeVersionState
        {
            Id = 1,
            TenantId = 0,
            AppVersion = currentAppVersion,
            DbVersion = "0.0.0",
            MinSupportVersion = minSupportVersion
        };
        return Task.FromResult(State);
    }

    public Task<UpgradeMigrationHistory?> GetLatestHistoryAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Histories.LastOrDefault());
    }

    public Task SetUpgradingAsync(UpgradeVersionState version, string nodeName, DateTimeOffset startTime, CancellationToken cancellationToken = default)
    {
        version.IsUpgrading = true;
        version.UpgradeNode = nodeName;
        version.UpgradeStartTime = startTime;
        return Task.CompletedTask;
    }

    public Task SetUpgradeCompletedAsync(UpgradeVersionState version, string appVersion, string dbVersion, CancellationToken cancellationToken = default)
    {
        version.AppVersion = appVersion;
        version.DbVersion = dbVersion;
        version.IsUpgrading = false;
        return Task.CompletedTask;
    }

    public Task SetUpgradeFailedAsync(UpgradeVersionState version, CancellationToken cancellationToken = default)
    {
        version.IsUpgrading = false;
        return Task.CompletedTask;
    }

    public Task UpdateDbVersionAsync(UpgradeVersionState version, string dbVersion, CancellationToken cancellationToken = default)
    {
        version.DbVersion = dbVersion;
        return Task.CompletedTask;
    }

    public Task AddMigrationHistoryAsync(UpgradeMigrationHistory history, CancellationToken cancellationToken = default)
    {
        Histories.Add(history);
        return Task.CompletedTask;
    }

    public Task<bool> HasMigrationHistoryAsync(string version, string scriptName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Histories.Any(history => history.Version == version && history.ScriptName == scriptName && history.Success));
    }

    public Task<bool> TryCreateBaselineAsync(string appVersion, string dbVersion, string minSupportVersion, CancellationToken cancellationToken = default)
    {
        if (State is not null)
        {
            return Task.FromResult(false);
        }

        State = new UpgradeVersionState
        {
            Id = 1,
            TenantId = 0,
            AppVersion = appVersion,
            DbVersion = dbVersion,
            MinSupportVersion = minSupportVersion
        };
        return Task.FromResult(true);
    }
}

/// <summary>
/// 记录每次取锁与释放的锁提供者
/// </summary>
internal sealed class RecordingUpgradeLockProvider : IUpgradeLockProvider
{
    private readonly List<RecordingLockToken> _tokens = [];

    /// <summary>
    /// 取过锁且全部已释放
    /// </summary>
    public bool AllReleased => _tokens.Count > 0 && _tokens.All(token => token.IsReleased);

    public Task<IUpgradeLockToken?> TryAcquireLockAsync(string resourceKey, TimeSpan expiry, string nodeName, CancellationToken cancellationToken = default)
    {
        var token = new RecordingLockToken(resourceKey, nodeName);
        _tokens.Add(token);
        return Task.FromResult<IUpgradeLockToken?>(token);
    }

    private sealed class RecordingLockToken(string resourceKey, string lockId) : IUpgradeLockToken
    {
        public string ResourceKey { get; } = resourceKey;

        public string LockId { get; } = lockId;

        public bool IsReleased { get; private set; }

        public Task ReleaseAsync()
        {
            IsReleased = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsReleased = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// 记录执行过的脚本内容
/// </summary>
internal sealed class RecordingMigrationExecutor : IUpgradeMigrationExecutor
{
    /// <summary>
    /// 依执行顺序的脚本内容
    /// </summary>
    public List<string> ExecutedSql { get; } = [];

    public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        ExecutedSql.Add(sql);
        return Task.CompletedTask;
    }
}
