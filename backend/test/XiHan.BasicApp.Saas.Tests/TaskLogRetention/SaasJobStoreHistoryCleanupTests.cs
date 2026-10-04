// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.Tasks;
using XiHan.Framework.Data.SqlSugar.Clients;

namespace XiHan.BasicApp.Saas.Tests.TaskLogRetention;

/// <summary>
/// 任务存储历史清理测试
/// </summary>
public sealed class SaasJobStoreHistoryCleanupTests : IDisposable
{
    private readonly TaskLogTestDatabase _database = new();

    /// <summary>
    /// 跨月删除过期历史，保留未过期的
    /// </summary>
    [Fact]
    public async Task CleanupHistory_DeletesExpiredRowsAcrossMonths()
    {
        var now = DateTimeOffset.UtcNow;
        _database.Insert(now.AddDays(-90));
        _database.Insert(now.AddDays(-45));
        _database.Insert(now.AddDays(-5));

        await CreateStore().CleanupHistoryAsync(30);

        var remaining = _database.AllCreatedTimes();
        Assert.Single(remaining);
        Assert.True(remaining[0] > now.AddDays(-6));
    }

    /// <summary>
    /// 创建时间晚于截止时间的行保留（删除条件为严格小于）
    /// </summary>
    [Fact]
    public async Task CleanupHistory_KeepsRowAtCutoff()
    {
        _database.Insert(DateTimeOffset.UtcNow.AddDays(-29));

        await CreateStore().CleanupHistoryAsync(30);

        Assert.Equal(1, _database.CountAll());
    }

    /// <summary>
    /// 不再把过期历史读进内存：只发条件删除，不发查询
    /// </summary>
    [Fact]
    public async Task CleanupHistory_DoesNotQueryRowsBeforeDeleting()
    {
        _database.Insert(DateTimeOffset.UtcNow.AddDays(-90));
        var selects = 0;
        _database.Client.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && sql.Contains("Sys_Task_Log", StringComparison.OrdinalIgnoreCase))
            {
                selects++;
            }
        };

        await CreateStore().CleanupHistoryAsync(30);

        _database.Client.Aop.OnLogExecuting = null;
        Assert.Equal(0, selects);
        Assert.Equal(0, _database.CountAll());
    }

    /// <summary>
    /// 保留天数为负时拒绝执行
    /// </summary>
    [Fact]
    public async Task CleanupHistory_NegativeRetention_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateStore().CleanupHistoryAsync(-1));
    }

    /// <summary>
    /// 释放测试库
    /// </summary>
    public void Dispose()
    {
        _database.Dispose();
    }

    private SaasJobStore CreateStore()
    {
        var resolver = new Mock<ISqlSugarClientResolver> { CallBase = true };
        resolver.Setup(value => value.GetClientForEntity(typeof(SysTaskLog))).Returns(_database.Client);
        resolver.Setup(value => value.GetCurrentClient()).Returns(_database.Client);

        var services = new ServiceCollection();
        services.AddSingleton(resolver.Object);
        var provider = services.BuildServiceProvider();

        return new SaasJobStore(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SaasJobStore>.Instance);
    }
}
