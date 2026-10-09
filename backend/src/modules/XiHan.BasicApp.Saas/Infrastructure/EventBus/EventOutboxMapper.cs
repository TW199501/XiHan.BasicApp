// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.Framework.EventBus.Abstractions.Distributed;

namespace XiHan.BasicApp.Saas.Infrastructure.EventBus;

/// <summary>
/// 出站事件信息与发件箱实体的映射
/// </summary>
public static class EventOutboxMapper
{
    /// <summary>
    /// 转换为待发送的发件箱实体
    /// </summary>
    /// <param name="info">出站事件信息</param>
    /// <returns>发件箱实体</returns>
    public static SysEventOutbox ToEntity(OutgoingEventInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return new SysEventOutbox(info.Id)
        {
            EventName = info.EventName,
            EventData = info.EventData,
            CreatedTime = EventInboxMapper.ToOffset(info.CreatedTime),
            ExtraProperties = info.ExtraProperties.Count == 0 ? null : JsonSerializer.Serialize(info.ExtraProperties),
            Status = SysEventOutbox.StatusPending
        };
    }

    /// <summary>
    /// 转换为出站事件信息
    /// </summary>
    /// <param name="entity">发件箱实体</param>
    /// <returns>出站事件信息</returns>
    public static OutgoingEventInfo ToEventInfo(SysEventOutbox entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var info = new OutgoingEventInfo(entity.BasicId, entity.EventName, entity.EventData, entity.CreatedTime.UtcDateTime);
        EventInboxMapper.CopyExtraProperties(entity.ExtraProperties, info.ExtraProperties);

        return info;
    }
}
