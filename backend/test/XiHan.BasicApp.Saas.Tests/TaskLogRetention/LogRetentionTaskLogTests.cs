// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using XiHan.BasicApp.Saas.Application.Services;
using XiHan.BasicApp.Saas.Domain.Configurations;
using XiHan.BasicApp.Saas.Domain.DomainServices;
using XiHan.BasicApp.Saas.Infrastructure.Tasks;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.BasicApp.Saas.Tests.TaskLogRetention;

/// <summary>
/// 日志保留清理覆盖任务执行历史的测试
/// </summary>
public sealed class LogRetentionTaskLogTests : IDisposable
{
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
    /// 释放测试库
    /// </summary>
    public void Dispose()
    {
        _database.Dispose();
    }

    private LogRetentionCleanupTask CreateTask(int retentionDays)
    {
        var resolver = new Mock<ISqlSugarClientResolver>();
        resolver.Setup(value => value.GetCurrentClient()).Returns(_database.Client);

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
}
