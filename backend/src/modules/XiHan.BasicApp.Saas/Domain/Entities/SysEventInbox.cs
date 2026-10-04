// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Routing;

namespace XiHan.BasicApp.Saas.Domain.Entities;

/// <summary>
/// 分布式事件收件箱
/// </summary>
/// <remarks>
/// 全局表，只建在平台主库；主键取事件自身的标识，以去重键唯一索引保证幂等。
/// </remarks>
[SugarTable(TableName = "Sys_Event_Inbox", TableDescription = "分布式事件收件箱表")]
[SugarIndex("UX_{table}_DeKe", nameof(DedupKey), OrderByType.Asc, true)]
[SugarIndex("IX_{table}_St_CrTi", nameof(Status), OrderByType.Asc, nameof(CreatedTime), OrderByType.Asc)]
[PlatformDataSource]
public class SysEventInbox : SugarEntity<Guid>
{
    /// <summary>
    /// 待处理
    /// </summary>
    public const int StatusPending = 0;

    /// <summary>
    /// 已领取
    /// </summary>
    public const int StatusClaimed = 1;

    /// <summary>
    /// 已处理
    /// </summary>
    public const int StatusProcessed = 2;

    /// <summary>
    /// 已丢弃
    /// </summary>
    public const int StatusDiscarded = 3;

    /// <summary>
    /// 构造函数
    /// </summary>
    public SysEventInbox() : base()
    {
    }

    /// <summary>
    /// 以事件标识构造
    /// </summary>
    /// <param name="basicId">事件标识</param>
    public SysEventInbox(Guid basicId) : base(basicId)
    {
    }

    /// <summary>
    /// 消息标识
    /// </summary>
    [SugarColumn(ColumnName = "Message_Id", Length = 256, IsNullable = true, ColumnDescription = "消息标识")]
    public string? MessageId { get; set; }

    /// <summary>
    /// 去重键
    /// </summary>
    [SugarColumn(ColumnName = "Dedup_Key", Length = 256, IsNullable = false, ColumnDescription = "去重键，有消息标识时等于消息标识")]
    public string DedupKey { get; set; } = string.Empty;

    /// <summary>
    /// 事件名称
    /// </summary>
    [SugarColumn(ColumnName = "Event_Name", Length = 256, IsNullable = false, ColumnDescription = "事件名称")]
    public string EventName { get; set; } = string.Empty;

    /// <summary>
    /// 序列化后的事件数据
    /// </summary>
    [SugarColumn(ColumnName = "Event_Data", IsNullable = false, ColumnDescription = "序列化后的事件数据")]
    public byte[] EventData { get; set; } = [];

    /// <summary>
    /// 入箱时刻
    /// </summary>
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, ColumnDescription = "入箱时刻")]
    public DateTimeOffset CreatedTime { get; set; }

    /// <summary>
    /// 扩展属性的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Extra_Properties", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "扩展属性的 JSON")]
    public string? ExtraProperties { get; set; }

    /// <summary>
    /// 处理状态
    /// </summary>
    [SugarColumn(ColumnName = "Status", IsNullable = false, ColumnDescription = "处理状态，0 待处理，1 已领取，2 已处理，3 已丢弃")]
    public int Status { get; set; }

    /// <summary>
    /// 重试次数
    /// </summary>
    [SugarColumn(ColumnName = "Retry_Count", IsNullable = false, ColumnDescription = "重试次数")]
    public int RetryCount { get; set; }

    /// <summary>
    /// 下次重试时刻
    /// </summary>
    [SugarColumn(ColumnName = "Next_Retry_Time", IsNullable = true, ColumnDescription = "下次重试时刻")]
    public DateTimeOffset? NextRetryTime { get; set; }

    /// <summary>
    /// 领取令牌
    /// </summary>
    [SugarColumn(ColumnName = "Claim_Token", Length = 64, IsNullable = true, ColumnDescription = "领取令牌")]
    public string? ClaimToken { get; set; }

    /// <summary>
    /// 领取时刻
    /// </summary>
    [SugarColumn(ColumnName = "Claim_Time", IsNullable = true, ColumnDescription = "领取时刻")]
    public DateTimeOffset? ClaimTime { get; set; }

    /// <summary>
    /// 完结时刻
    /// </summary>
    [SugarColumn(ColumnName = "Handled_Time", IsNullable = true, ColumnDescription = "完结时刻")]
    public DateTimeOffset? HandledTime { get; set; }
}
