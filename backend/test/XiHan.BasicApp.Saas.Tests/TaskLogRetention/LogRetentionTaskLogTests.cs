// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using XiHan.BasicApp.Saas.Application.Services;
using XiHan.BasicApp.Saas.Domain.Configurations;
using XiHan.BasicApp.Saas.Domain.DomainServices;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.Tasks;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.BasicApp.Saas.Tests.TaskLogRetention;

/// <summary>
/// 日志保留清理覆盖任务执行历史的测试
/// </summary>
public sealed class LogRetentionTaskLogTests : IDisposable
{
    private const long IsolatedTenantId = 7;

    private readonly TaskLogTestDatabase _database = new();

    /// <summary>
    /// 跨月的过期任务历史被删除，未过期的保留
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DeletesExpiredTaskLogsAcrossMonths()
    {
        var now = DateTimeOffset.UtcNow;
        _database.Insert(now.AddDays(-250));
        _database.Insert(now.AddDays(-200));
        _database.Insert(now.AddDays(-1));
        var task = CreateTask(retentionDays: 180);

        await task.ExecuteAsync();

        var remaining = _database.AllCreatedTimes();
        Assert.Single(remaining);
        Assert.True(remaining[0] > now.AddDays(-2));
    }

    /// <summary>
    /// 摘要计入删除的任务历史行数
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SummaryCountsDeletedTaskLogs()
    {
        var now = DateTimeOffset.UtcNow;
        _database.Insert(now.AddDays(-200));
        _database.Insert(now.AddDays(-190));
        var task = CreateTask(retentionDays: 180);

        var summary = await task.ExecuteAsync();

        Assert.Contains("共删除 2 行", summary, StringComparison.Ordinal);
        Assert.Equal(0, _database.CountAll());
    }

    /// <summary>
    /// 库隔离租户的任务历史在平台库中按该租户清理：平台与租户 7 的过期行都删除，未过期的保留
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DeletesExpiredTaskLogsOfDatabaseIsolatedTenantInPlatformDatabase()
    {
        using var platform = new TaskLogTestDatabase(enableTenantFilter: true);
        using var tenantDatabase = new TaskLogTestDatabase(enableTenantFilter: true);
        var now = DateTimeOffset.UtcNow;
        platform.Insert(now.AddDays(-200), tenantId: 0);
        platform.Insert(now.AddDays(-1), tenantId: 0);
        platform.Insert(now.AddDays(-210), tenantId: IsolatedTenantId);
        platform.Insert(now.AddDays(-2), tenantId: IsolatedTenantId);
        var task = CreateIsolatedTenantTask(platform, tenantDatabase, [null, IsolatedTenantId]);

        await task.ExecuteAsync();

        var remaining = platform.AllRows();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, row => row.TenantId == 0 && row.CreatedTime > now.AddDays(-2));
        Assert.Contains(remaining, row => row.TenantId == IsolatedTenantId && row.CreatedTime > now.AddDays(-3));
    }

    /// <summary>
    /// 平台作用域只删除平台自己的过期任务历史，不删除库隔离租户的行
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_PlatformScopeKeepsIsolatedTenantTaskLogs()
    {
        using var platform = new TaskLogTestDatabase(enableTenantFilter: true);
        using var tenantDatabase = new TaskLogTestDatabase(enableTenantFilter: true);
        var now = DateTimeOffset.UtcNow;
        platform.Insert(now.AddDays(-200), tenantId: 0);
        platform.Insert(now.AddDays(-210), tenantId: IsolatedTenantId);
        var task = CreateIsolatedTenantTask(platform, tenantDatabase, [null]);

        await task.ExecuteAsync();

        var remaining = platform.AllRows();
        Assert.Single(remaining);
        Assert.Equal(IsolatedTenantId, remaining[0].TenantId);
    }

    /// <summary>
    /// 释放测试库
    /// </summary>
    public void Dispose()
    {
        _database.Dispose();
    }

    private LogRetentionCleanupTask CreateTask(int retentionDays)
    {
        var resolver = new Mock<ISqlSugarClientResolver> { CallBase = true };
        resolver.Setup(value => value.GetCurrentClient()).Returns(_database.Client);
        resolver.Setup(value => value.GetClientForEntity(It.IsAny<Type>())).Returns(_database.Client);

        var configuration = new Mock<ISaasConfigurationService>();
        configuration
            .Setup(value => value.GetJsonAsync(SaasConfigKeys.Log.RetentionDays, LogRetentionCleanupTask.DefaultRetentionDays, It.IsAny<CancellationToken>()))
            .ReturnsAsync(retentionDays);

        var scopeRunner = new Mock<ITenantDataScopeRunner>();
        scopeRunner
            .Setup(value => value.RunAsync(It.IsAny<Func<long?, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<long?, Task>, CancellationToken>((action, _) => action(null));

        var currentTenant = new Mock<ICurrentTenant>();
        currentTenant.Setup(value => value.Change(It.IsAny<long?>(), It.IsAny<string?>())).Returns(Mock.Of<IDisposable>());

        return new LogRetentionCleanupTask(resolver.Object, configuration.Object, scopeRunner.Object, currentTenant.Object, NullLogger<LogRetentionCleanupTask>.Instance);
    }

    /// <summary>
    /// 创建库隔离场景的清理任务：按作用域切换两库的作用域租户；租户 7 作用域的当前库是它自己的空库，
    /// SysTaskLog 的实体库始终是平台库，其它实体的实体库等于当前库
    /// </summary>
    private static LogRetentionCleanupTask CreateIsolatedTenantTask(TaskLogTestDatabase platform, TaskLogTestDatabase tenantDatabase, long?[] scopes)
    {
        long? currentScope = null;

        var resolver = new Mock<ISqlSugarClientResolver> { CallBase = true };
        resolver.Setup(value => value.GetCurrentClient())
            .Returns(() => currentScope == IsolatedTenantId ? tenantDatabase.Client : platform.Client);
        resolver.Setup(value => value.GetClientForEntity(It.IsAny<Type>()))
            .Returns(() => currentScope == IsolatedTenantId ? tenantDatabase.Client : platform.Client);
        resolver.Setup(value => value.GetClientForEntity(typeof(SysTaskLog))).Returns(platform.Client);

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
                    currentScope = scope;
                    platform.ScopeTenantId = scope ?? 0;
                    tenantDatabase.ScopeTenantId = scope ?? 0;
                    await action(scope);
                }

                currentScope = null;
                platform.ScopeTenantId = 0;
                tenantDatabase.ScopeTenantId = 0;
            });

        var currentTenant = new Mock<ICurrentTenant>();
        currentTenant.Setup(value => value.Change(It.IsAny<long?>(), It.IsAny<string?>())).Returns(Mock.Of<IDisposable>());

        return new LogRetentionCleanupTask(resolver.Object, configuration.Object, scopeRunner.Object, currentTenant.Object, NullLogger<LogRetentionCleanupTask>.Instance);
    }
}
