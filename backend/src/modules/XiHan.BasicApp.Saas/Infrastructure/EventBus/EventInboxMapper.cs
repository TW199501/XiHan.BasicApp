// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.Framework.EventBus.Abstractions.Distributed;

namespace XiHan.BasicApp.Saas.Infrastructure.EventBus;

/// <summary>
/// 入站事件信息与收件箱实体的映射
/// </summary>
public static class EventInboxMapper
{
    /// <summary>
    /// 没有消息标识时去重键的前缀
    /// </summary>
    public const string NoMessageIdKeyPrefix = "no-message-id:";

    /// <summary>
    /// 转换为待处理的收件箱实体
    /// </summary>
    /// <param name="info">入站事件信息</param>
    /// <returns>收件箱实体</returns>
    public static SysEventInbox ToEntity(IncomingEventInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var messageId = string.IsNullOrWhiteSpace(info.MessageId) ? null : info.MessageId;

        return new SysEventInbox(info.Id)
        {
            MessageId = messageId,
            DedupKey = messageId ?? $"{NoMessageIdKeyPrefix}{info.Id:N}",
            EventName = info.EventName,
            EventData = info.EventData,
            CreatedTime = ToOffset(info.CreatedTime),
            ExtraProperties = info.ExtraProperties.Count == 0 ? null : JsonSerializer.Serialize(info.ExtraProperties),
            Status = SysEventInbox.StatusPending
        };
    }

    /// <summary>
    /// 转换为入站事件信息
    /// </summary>
    /// <param name="entity">收件箱实体</param>
    /// <returns>入站事件信息</returns>
    public static IncomingEventInfo ToEventInfo(SysEventInbox entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var info = new IncomingEventInfo(entity.BasicId, entity.MessageId ?? string.Empty, entity.EventName, entity.EventData, entity.CreatedTime.UtcDateTime);
        CopyExtraProperties(entity.ExtraProperties, info.ExtraProperties);

        return info;
    }

    /// <summary>
    /// 把 DateTime 转为偏移时间，未指定种类时按 UTC 处理
    /// </summary>
    /// <param name="value">时间</param>
    /// <returns>偏移时间</returns>
    internal static DateTimeOffset ToOffset(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(value),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero)
        };
    }

    /// <summary>
    /// 把扩展属性 JSON 写入目标字典
    /// </summary>
    /// <param name="json">扩展属性 JSON</param>
    /// <param name="target">目标字典</param>
    internal static void CopyExtraProperties(string? json, IDictionary<string, object?> target)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        var properties = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        if (properties is null)
        {
            return;
        }

        foreach (var pair in properties)
        {
            target[pair.Key] = pair.Value;
        }
    }
}
