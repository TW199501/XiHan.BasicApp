// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.EventBus;
using XiHan.Framework.EventBus.Abstractions.Distributed;

namespace XiHan.BasicApp.Saas.Tests.EventBox;

/// <summary>
/// 发件箱 SqlSugar 实现测试
/// </summary>
public sealed class SaasEventOutboxTests : IDisposable
{
    private readonly EventBoxTestContext _context = new();

    /// <summary>
    /// 入箱后可被领取
    /// </summary>
    [Fact]
    public async Task Enqueue_ThenClaim_ReturnsEvent()
    {
        var outbox = _context.CreateOutbox();
        var info = NewEvent();

        await outbox.EnqueueAsync(info);
        var claimed = await outbox.GetWaitingEventsAsync(10);

        Assert.Single(claimed);
        Assert.Equal(info.Id, claimed[0].Id);
    }

    /// <summary>
    /// 单次领取不超过 maxCount
    /// </summary>
    [Fact]
    public async Task Claim_RespectsMaxCount()
    {
        var outbox = _context.CreateOutbox();
        for (var i = 0; i < 5; i++)
        {
            await outbox.EnqueueAsync(NewEvent());
        }

        Assert.Equal(3, (await outbox.GetWaitingEventsAsync(3)).Count);
        Assert.Equal(2, (await outbox.GetWaitingEventsAsync(3)).Count);
    }

    /// <summary>
    /// 两个实例领取的记录互不重叠
    /// </summary>
    [Fact]
    public async Task Claim_ByTwoInstances_DoesNotOverlap()
    {
        var first = _context.CreateOutbox();
        var second = _context.CreateOutbox();
        for (var i = 0; i < 4; i++)
        {
            await first.EnqueueAsync(NewEvent());
        }

        var a = await first.GetWaitingEventsAsync(2);
        var b = await second.GetWaitingEventsAsync(10);

        Assert.Empty(a.Select(e => e.Id).Intersect(b.Select(e => e.Id)));
        Assert.Equal(4, a.Count + b.Count);
    }

    /// <summary>
    /// 超过领取超时的记录可被重新领取
    /// </summary>
    [Fact]
    public async Task Claim_AfterClaimTimeout_ReclaimsStaleRow()
    {
        var options = new SaasEventBoxOptions { ClaimTimeout = TimeSpan.FromMinutes(5) };
        var outbox = _context.CreateOutbox(options);
        var info = NewEvent();
        await outbox.EnqueueAsync(info);
        await outbox.GetWaitingEventsAsync(10);

        Assert.Empty(await outbox.GetWaitingEventsAsync(10));

        _context.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var reclaimed = await outbox.GetWaitingEventsAsync(10);

        Assert.Equal(info.Id, Assert.Single(reclaimed).Id);
    }

    /// <summary>
    /// 不支持 filter 参数
    /// </summary>
    [Fact]
    public async Task Claim_WithFilter_ThrowsNotSupported()
    {
        var outbox = _context.CreateOutbox();

        await Assert.ThrowsAsync<NotSupportedException>(() => outbox.GetWaitingEventsAsync(10, e => e.EventName == "x"));
    }

    /// <summary>
    /// 按标识删除
    /// </summary>
    [Fact]
    public async Task Delete_RemovesRow()
    {
        var outbox = _context.CreateOutbox();
        var info = NewEvent();
        await outbox.EnqueueAsync(info);

        await outbox.DeleteAsync(info.Id);

        Assert.Equal(0, await _context.Client.Queryable<SysEventOutbox>().CountAsync());
    }

    /// <summary>
    /// 批量删除只删指定标识
    /// </summary>
    [Fact]
    public async Task DeleteMany_RemovesRows()
    {
        var outbox = _context.CreateOutbox();
        var a = NewEvent();
        var b = NewEvent();
        var c = NewEvent();
        await outbox.EnqueueAsync(a);
        await outbox.EnqueueAsync(b);
        await outbox.EnqueueAsync(c);

        await outbox.DeleteManyAsync([a.Id, b.Id]);

        var remaining = await _context.Client.Queryable<SysEventOutbox>().Select(e => e.BasicId).ToListAsync();
        Assert.Equal(c.Id, Assert.Single(remaining));
    }

    /// <summary>
    /// 独立库租户入箱被拒绝且不写入
    /// </summary>
    [Fact]
    public async Task Enqueue_InIndependentTenantLayout_Throws()
    {
        var outbox = _context.CreateOutbox();
        _context.Resolver.IndependentTenantIds.Add(5);

        using (_context.Tenant.Change(5))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => outbox.EnqueueAsync(NewEvent()));
            Assert.Contains("租户独立库", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, await _context.Client.Queryable<SysEventOutbox>().CountAsync());
    }

    /// <summary>
    /// 共享库租户正常入箱
    /// </summary>
    [Fact]
    public async Task Enqueue_InSharedTenantLayout_Succeeds()
    {
        var outbox = _context.CreateOutbox();

        using (_context.Tenant.Change(6))
        {
            await outbox.EnqueueAsync(NewEvent());
        }

        Assert.Equal(1, await _context.Client.Queryable<SysEventOutbox>().CountAsync());
    }

    /// <summary>
    /// 事务回滚后不留下事件
    /// </summary>
    [Fact]
    public async Task Enqueue_InRolledBackTransaction_LeavesNoRow()
    {
        var outbox = _context.CreateOutbox();

        _context.Client.Ado.BeginTran();
        await outbox.EnqueueAsync(NewEvent());
        _context.Client.Ado.RollbackTran();

        Assert.Equal(0, await _context.Client.Queryable<SysEventOutbox>().CountAsync());
    }

    /// <summary>
    /// 释放测试上下文
    /// </summary>
    public void Dispose()
    {
        _context.Dispose();
    }

    private OutgoingEventInfo NewEvent()
    {
        _context.Clock.Advance(TimeSpan.FromMilliseconds(1));
        return new OutgoingEventInfo(Guid.NewGuid(), "order.created", [1, 2, 3], _context.Clock.GetUtcNow().UtcDateTime);
    }
}
