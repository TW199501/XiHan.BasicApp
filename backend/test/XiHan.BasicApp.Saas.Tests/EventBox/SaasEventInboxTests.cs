// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.EventBus;
using XiHan.Framework.EventBus.Abstractions.Distributed;

namespace XiHan.BasicApp.Saas.Tests.EventBox;

/// <summary>
/// 收件箱 SqlSugar 实现测试
/// </summary>
public sealed class SaasEventInboxTests : IDisposable
{
    private readonly EventBoxTestContext _context = new();

    /// <summary>
    /// 同一消息标识只入箱一次
    /// </summary>
    [Fact]
    public async Task Enqueue_SameMessageIdTwice_StoresOnce()
    {
        var inbox = _context.CreateInbox();

        await inbox.EnqueueAsync(NewEvent("msg-1"));
        await inbox.EnqueueAsync(NewEvent("msg-1"));

        Assert.Equal(1, await _context.Client.Queryable<SysEventInbox>().CountAsync());
    }

    /// <summary>
    /// 事务内重复入箱时直接抛出原始插入异常，不再做去重查询
    /// </summary>
    [Fact]
    public async Task Enqueue_DuplicateInsideTransaction_ThrowsOriginalException()
    {
        var inbox = _context.CreateInbox();
        await inbox.EnqueueAsync(NewEvent("msg-x"));

        _context.Client.Ado.BeginTran();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => inbox.EnqueueAsync(NewEvent("msg-x")));
        }
        finally
        {
            _context.Client.Ado.RollbackTran();
        }
    }

    /// <summary>
    /// 入箱后可按消息标识查到
    /// </summary>
    [Fact]
    public async Task ExistsByMessageId_ReturnsTrueAfterEnqueue()
    {
        var inbox = _context.CreateInbox();
        await inbox.EnqueueAsync(NewEvent("msg-2"));

        Assert.True(await inbox.ExistsByMessageIdAsync("msg-2"));
        Assert.False(await inbox.ExistsByMessageIdAsync("msg-x"));
    }

    /// <summary>
    /// 未到重试时刻的记录不被领取
    /// </summary>
    [Fact]
    public async Task Claim_SkipsRowsWithFutureNextRetryTime()
    {
        var inbox = _context.CreateInbox();
        var info = NewEvent("msg-3");
        await inbox.EnqueueAsync(info);
        await inbox.GetWaitingEventsAsync(10);

        await inbox.RetryLaterAsync(info.Id, 1, _context.Clock.GetUtcNow().UtcDateTime.AddMinutes(1));

        Assert.Empty(await inbox.GetWaitingEventsAsync(10));

        _context.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Single(await inbox.GetWaitingEventsAsync(10));
    }

    /// <summary>
    /// 两个实例领取的记录互不重叠
    /// </summary>
    [Fact]
    public async Task Claim_ByTwoInstances_DoesNotOverlap()
    {
        var first = _context.CreateInbox();
        var second = _context.CreateInbox();
        for (var i = 0; i < 4; i++)
        {
            await first.EnqueueAsync(NewEvent($"m-{i}"));
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
        var inbox = _context.CreateInbox(new SaasEventBoxOptions { ClaimTimeout = TimeSpan.FromMinutes(5) });
        var info = NewEvent("msg-4");
        await inbox.EnqueueAsync(info);
        await inbox.GetWaitingEventsAsync(10);

        Assert.Empty(await inbox.GetWaitingEventsAsync(10));

        _context.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Equal(info.Id, Assert.Single(await inbox.GetWaitingEventsAsync(10)).Id);
    }

    /// <summary>
    /// 已处理的记录不再被领取
    /// </summary>
    [Fact]
    public async Task MarkAsProcessed_ThenNotClaimedAgain()
    {
        var inbox = _context.CreateInbox();
        var info = NewEvent("msg-5");
        await inbox.EnqueueAsync(info);
        await inbox.GetWaitingEventsAsync(10);

        await inbox.MarkAsProcessedAsync(info.Id);
        _context.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Empty(await inbox.GetWaitingEventsAsync(10));
    }

    /// <summary>
    /// 已处理的记录不会被晚到的重试调用改回待处理
    /// </summary>
    [Fact]
    public async Task RetryLater_OnProcessedRow_DoesNotRevert()
    {
        var inbox = _context.CreateInbox();
        var info = NewEvent("msg-6");
        await inbox.EnqueueAsync(info);
        await inbox.GetWaitingEventsAsync(10);
        await inbox.MarkAsProcessedAsync(info.Id);

        await inbox.RetryLaterAsync(info.Id, 1, null);

        var row = await _context.Client.Queryable<SysEventInbox>().FirstAsync(e => e.BasicId == info.Id);
        Assert.Equal(SysEventInbox.StatusProcessed, row.Status);
    }

    /// <summary>
    /// 只清理超过保留期的已完结记录
    /// </summary>
    [Fact]
    public async Task DeleteOldEvents_RemovesOnlyExpiredHandledRows()
    {
        var inbox = _context.CreateInbox(new SaasEventBoxOptions { InboxRetentionPeriod = TimeSpan.FromDays(7) });
        var expired = NewEvent("old");
        await inbox.EnqueueAsync(expired);
        await inbox.GetWaitingEventsAsync(10);
        await inbox.MarkAsProcessedAsync(expired.Id);

        _context.Clock.Advance(TimeSpan.FromDays(8));

        var recent = NewEvent("recent");
        await inbox.EnqueueAsync(recent);
        await inbox.GetWaitingEventsAsync(10);
        await inbox.MarkAsProcessedAsync(recent.Id);
        var pending = NewEvent("pending");
        await inbox.EnqueueAsync(pending);

        await inbox.DeleteOldEventsAsync();

        var ids = await _context.Client.Queryable<SysEventInbox>().Select(e => e.BasicId).ToListAsync();
        Assert.DoesNotContain(expired.Id, ids);
        Assert.Contains(recent.Id, ids);
        Assert.Contains(pending.Id, ids);
    }

    /// <summary>
    /// 释放测试上下文
    /// </summary>
    public void Dispose()
    {
        _context.Dispose();
    }

    private IncomingEventInfo NewEvent(string messageId)
    {
        _context.Clock.Advance(TimeSpan.FromMilliseconds(1));
        return new IncomingEventInfo(Guid.NewGuid(), messageId, "order.created", [1, 2, 3], _context.Clock.GetUtcNow().UtcDateTime);
    }
}
