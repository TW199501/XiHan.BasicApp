// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.EventBus;
using XiHan.Framework.EventBus.Abstractions.Distributed;

namespace XiHan.BasicApp.Saas.Tests.EventBox;

/// <summary>
/// 收发件箱实体、映射与建表测试
/// </summary>
public sealed class EventBoxMapperTests : IDisposable
{
    private readonly EventBoxTestContext _context = new();

    /// <summary>
    /// 发件箱映射往返保留全部字段，入箱状态为待发送
    /// </summary>
    [Fact]
    public void Outbox_RoundTrip_PreservesFields()
    {
        var info = new OutgoingEventInfo(Guid.NewGuid(), "order.created", [1, 2, 3], new DateTime(2026, 10, 4, 1, 2, 3, DateTimeKind.Utc));
        info.ExtraProperties["traceId"] = "abc";

        var entity = EventOutboxMapper.ToEntity(info);
        var restored = EventOutboxMapper.ToEventInfo(entity);

        Assert.Equal(SysEventOutbox.StatusPending, entity.Status);
        Assert.Equal(info.Id, restored.Id);
        Assert.Equal(info.EventName, restored.EventName);
        Assert.Equal(info.EventData, restored.EventData);
        Assert.Equal(info.CreatedTime, restored.CreatedTime);
        Assert.Equal("abc", restored.ExtraProperties["traceId"]?.ToString());
    }

    /// <summary>
    /// 有消息标识时以消息标识作去重键
    /// </summary>
    [Fact]
    public void Inbox_WithMessageId_UsesMessageIdAsDedupKey()
    {
        var info = new IncomingEventInfo(Guid.NewGuid(), "msg-1", "order.created", [1], DateTime.UtcNow);

        var entity = EventInboxMapper.ToEntity(info);

        Assert.Equal("msg-1", entity.DedupKey);
        Assert.Equal("msg-1", entity.MessageId);
    }

    /// <summary>
    /// 没有消息标识时以事件标识作去重键
    /// </summary>
    [Fact]
    public void Inbox_WithoutMessageId_UsesIdAsDedupKey()
    {
        var id = Guid.NewGuid();
        var info = new IncomingEventInfo(id, string.Empty, "order.created", [1], DateTime.UtcNow);

        var entity = EventInboxMapper.ToEntity(info);

        Assert.Equal($"{EventInboxMapper.NoMessageIdKeyPrefix}{id:N}", entity.DedupKey);
        Assert.Null(entity.MessageId);
    }

    /// <summary>
    /// 建表后两张表都存在
    /// </summary>
    [Fact]
    public void InitTables_CreatesBothTables()
    {
        Assert.True(_context.Client.DbMaintenance.IsAnyTable("Sys_Event_Outbox", false));
        Assert.True(_context.Client.DbMaintenance.IsAnyTable("Sys_Event_Inbox", false));
    }

    /// <summary>
    /// 去重键唯一索引拒绝重复插入
    /// </summary>
    [Fact]
    public void Inbox_DuplicateDedupKey_ViolatesUniqueIndex()
    {
        var first = EventInboxMapper.ToEntity(new IncomingEventInfo(Guid.NewGuid(), "dup", "e", [1], DateTime.UtcNow));
        var second = EventInboxMapper.ToEntity(new IncomingEventInfo(Guid.NewGuid(), "dup", "e", [1], DateTime.UtcNow));
        _context.Client.Insertable(first).ExecuteCommand();

        Assert.ThrowsAny<Exception>(() => _context.Client.Insertable(second).ExecuteCommand());
    }

    /// <summary>
    /// 释放测试上下文
    /// </summary>
    public void Dispose()
    {
        _context.Dispose();
    }
}
