// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.Framework.Data.SqlSugar.Entities;
using XiHan.Framework.Data.SqlSugar.Routing;

namespace XiHan.BasicApp.Saas.Domain.Entities;

/// <summary>
/// 分布式事件发件箱
/// </summary>
/// <remarks>
/// 全局表，只建在平台主库；主键取事件自身的标识。
/// </remarks>
[SugarTable(TableName = "Sys_Event_Outbox", TableDescription = "分布式事件发件箱表")]
[SugarIndex("IX_{table}_St_CrTi", nameof(Status), OrderByType.Asc, nameof(CreatedTime), OrderByType.Asc)]
[PlatformDataSource]
public class SysEventOutbox : SugarEntity<Guid>
{
    /// <summary>
    /// 待发送
    /// </summary>
    public const int StatusPending = 0;

    /// <summary>
    /// 已领取
    /// </summary>
    public const int StatusClaimed = 1;

    /// <summary>
    /// 构造函数
    /// </summary>
    public SysEventOutbox() : base()
    {
    }

    /// <summary>
    /// 以事件标识构造
    /// </summary>
    /// <param name="basicId">事件标识</param>
    public SysEventOutbox(Guid basicId) : base(basicId)
    {
    }

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
    /// 事件创建时间
    /// </summary>
    [SugarColumn(ColumnName = "Created_Time", IsNullable = false, ColumnDescription = "事件创建时间")]
    public DateTimeOffset CreatedTime { get; set; }

    /// <summary>
    /// 扩展属性的 JSON
    /// </summary>
    [SugarColumn(ColumnName = "Extra_Properties", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true, ColumnDescription = "扩展属性的 JSON")]
    public string? ExtraProperties { get; set; }

    /// <summary>
    /// 发送状态
    /// </summary>
    [SugarColumn(ColumnName = "Status", IsNullable = false, ColumnDescription = "发送状态，0 待发送，1 已领取")]
    public int Status { get; set; }

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
}
