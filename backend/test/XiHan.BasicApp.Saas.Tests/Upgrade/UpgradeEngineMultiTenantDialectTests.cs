// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using XiHan.BasicApp.Saas.Infrastructure.Upgrade;
using XiHan.Framework.Core.Application;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.MultiTenancy;
using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Upgrade.Abstractions;
using XiHan.Framework.Upgrade.Enums;
using XiHan.Framework.Upgrade.Models;
using XiHan.Framework.Upgrade.Options;
using XiHan.Framework.Upgrade.Services;

namespace XiHan.BasicApp.Saas.Tests.Upgrade;

/// <summary>
/// 多租户升级：PostgreSQL 平台库与 SQL Server 租户库各执行各自方言的脚本
/// </summary>
public sealed class UpgradeEngineMultiTenantDialectTests : IDisposable
{
    private const long TenantId = 2;

    private readonly TempUpgradeScriptTree _tree = new();
    private readonly CurrentTenant _currentTenant = new(AsyncLocalCurrentTenantAccessor.Instance);
    private readonly InMemoryUpgradeVersionStore _platformStore = new();
    private readonly InMemoryUpgradeVersionStore _tenantStore = new();
    private readonly RecordingUpgradeLockProvider _lock = new();
    private readonly List<(long? TenantId, string Sql)> _executed = [];

    public UpgradeEngineMultiTenantDialectTests()
    {
        _platformStore.Seed("5.6.0");
        _tenantStore.Seed("5.6.0");
        _tenantStore.State!.TenantId = TenantId;
    }

    public void Dispose()
    {
        _tree.Dispose();
    }

    [Fact]
    public async Task 平台库与租户库各执行各自方言并各自推进版本()
    {
        _tree.Add("5.6.0/5.6.0.sql", "pg-5.6.0");
        _tree.Add("5.7.0/pgsql/5.7.0.sql", "pg-5.7.0");
        _tree.Add("5.7.0/mssql/5.7.0.sql", "mssql-5.7.0");
        _tree.Add("5.7.0/mysql/5.7.0.sql", "mysql-5.7.0");

        var result = await CreateEngine().ExecuteAsync();

        Assert.Equal(UpgradeStatus.Completed, result.Status);
        Assert.Equal([(null, "pg-5.7.0"), (TenantId, "mssql-5.7.0")], _executed);
        Assert.Equal("5.7.0", _platformStore.State?.DbVersion);
        Assert.Equal("5.7.0", _tenantStore.State?.DbVersion);
        Assert.Equal("5.7.0.sql", Assert.Single(_platformStore.Histories).ScriptName);
        Assert.Equal("5.7.0.sql", Assert.Single(_tenantStore.Histories).ScriptName);
        Assert.True(_lock.AllReleased);
    }

    [Fact]
    public async Task 租户库缺方言时租户失败_释放锁_先行的平台库照常完成()
    {
        _tree.Add("5.6.0/5.6.0.sql", "pg-5.6.0");
        _tree.Add("5.7.0/5.7.0.sql", "pg-5.7.0");

        var result = await CreateEngine().ExecuteAsync();

        Assert.Equal(UpgradeStatus.Failed, result.Status);
        Assert.Contains(Path.Combine("5.7.0", "mssql", UpgradeScriptCatalog.MissingScriptFileName), result.Message);
        Assert.Equal([(null, "pg-5.7.0")], _executed);
        Assert.Equal("5.7.0", _platformStore.State?.DbVersion);
        Assert.Single(_platformStore.Histories);
        Assert.Equal("5.6.0", _tenantStore.State?.DbVersion);
        Assert.False(_tenantStore.State?.IsUpgrading);
        Assert.Empty(_tenantStore.Histories);
        Assert.True(_lock.AllReleased);
    }

    /// <summary>
    /// 按当前租户切换数据库类型、版本存储与执行记录的真实升级引擎
    /// </summary>
    private UpgradeEngine CreateEngine()
    {
        var postgreSql = Mock.Of<ISqlSugarClient>(sugar => sugar.CurrentConnectionConfig == new ConnectionConfig { DbType = DbType.PostgreSQL });
        var sqlServer = Mock.Of<ISqlSugarClient>(sugar => sugar.CurrentConnectionConfig == new ConnectionConfig { DbType = DbType.SqlServer });
        var resolver = new Mock<ISqlSugarClientResolver>();
        resolver.Setup(r => r.GetCurrentClient()).Returns(() => _currentTenant.Id == TenantId ? sqlServer : postgreSql);

        var tenantProvider = new Mock<IUpgradeTenantProvider>();
        tenantProvider.Setup(provider => provider.GetTenants()).Returns([new BasicTenantInfo(null, "平台库"), new BasicTenantInfo(TenantId, "租户")]);

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(provider => provider.GetService(typeof(ICurrentTenant))).Returns(_currentTenant);

        var options = Options.Create(new XiHanUpgradeOptions
        {
            AppVersion = UpgradeTestDoubles.AppVersion,
            NodeName = "test-node",
            MigrationsRootPath = _tree.RootPath,
            EnableMaintenanceMode = false,
            EnableMultiTenantIsolation = true
        });

        var executor = new Mock<IUpgradeMigrationExecutor>();
        executor.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((sql, _) => _executed.Add((_currentTenant.Id, sql)))
            .Returns(Task.CompletedTask);

        return new UpgradeEngine(
            new TenantRoutingVersionStore(() => _currentTenant.Id == TenantId ? _tenantStore : _platformStore),
            [new DialectAwareUpgradeScriptProvider(options, resolver.Object)],
            _lock,
            Mock.Of<IUpgradeMaintenanceModeManager>(),
            Mock.Of<IUpgradeFileUpdater>(),
            Mock.Of<IRollingRestartCoordinator>(),
            tenantProvider.Object,
            executor.Object,
            serviceProvider.Object,
            Mock.Of<IApplicationInfoAccessor>(),
            options,
            NullLogger<UpgradeEngine>.Instance);
    }

    /// <summary>
    /// 按当前租户转发到对应库的版本存储
    /// </summary>
    private sealed class TenantRoutingVersionStore(Func<IUpgradeVersionStore> current) : IUpgradeVersionStore
    {
        public Task EnsureTablesAsync(CancellationToken cancellationToken = default)
        {
            return current().EnsureTablesAsync(cancellationToken);
        }

        public Task<UpgradeVersionState> GetOrCreateAsync(string currentAppVersion, string minSupportVersion, CancellationToken cancellationToken = default)
        {
            return current().GetOrCreateAsync(currentAppVersion, minSupportVersion, cancellationToken);
        }

        public Task<UpgradeMigrationHistory?> GetLatestHistoryAsync(CancellationToken cancellationToken = default)
        {
            return current().GetLatestHistoryAsync(cancellationToken);
        }

        public Task SetUpgradingAsync(UpgradeVersionState version, string nodeName, DateTimeOffset startTime, CancellationToken cancellationToken = default)
        {
            return current().SetUpgradingAsync(version, nodeName, startTime, cancellationToken);
        }

        public Task SetUpgradeCompletedAsync(UpgradeVersionState version, string appVersion, string dbVersion, CancellationToken cancellationToken = default)
        {
            return current().SetUpgradeCompletedAsync(version, appVersion, dbVersion, cancellationToken);
        }

        public Task SetUpgradeFailedAsync(UpgradeVersionState version, CancellationToken cancellationToken = default)
        {
            return current().SetUpgradeFailedAsync(version, cancellationToken);
        }

        public Task UpdateDbVersionAsync(UpgradeVersionState version, string dbVersion, CancellationToken cancellationToken = default)
        {
            return current().UpdateDbVersionAsync(version, dbVersion, cancellationToken);
        }

        public Task AddMigrationHistoryAsync(UpgradeMigrationHistory history, CancellationToken cancellationToken = default)
        {
            return current().AddMigrationHistoryAsync(history, cancellationToken);
        }

        public Task<bool> HasMigrationHistoryAsync(string version, string scriptName, CancellationToken cancellationToken = default)
        {
            return current().HasMigrationHistoryAsync(version, scriptName, cancellationToken);
        }

        public Task<bool> TryCreateBaselineAsync(string appVersion, string dbVersion, string minSupportVersion, CancellationToken cancellationToken = default)
        {
            return current().TryCreateBaselineAsync(appVersion, dbVersion, minSupportVersion, cancellationToken);
        }
    }
}
