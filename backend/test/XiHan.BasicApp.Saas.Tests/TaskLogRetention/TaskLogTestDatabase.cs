// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Data.Sqlite;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.Framework.Domain.Entities.Abstracts;

namespace XiHan.BasicApp.Saas.Tests.TaskLogRetention;

/// <summary>
/// 任务执行历史测试库：共享内存 SQLite，按需建立按月分表
/// </summary>
/// <remarks>
/// 开启租户过滤时按框架 ApplySugarGlobalFilters 的口径注册全局过滤器：
/// IMultiTenantEntity 为 TenantId = 0 或等于作用域租户，IStrictMultiTenantEntity 为 TenantId 等于作用域租户；
/// 删除与更新自动带上过滤（IsAutoDeleteQueryFilter / IsAutoUpdateQueryFilter）。作用域租户取 <see cref="ScopeTenantId"/>。
/// </remarks>
internal sealed class TaskLogTestDatabase : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private long _nextId = 1;

    /// <summary>
    /// 建库
    /// </summary>
    /// <param name="enableTenantFilter">是否注册租户全局过滤器</param>
    public TaskLogTestDatabase(bool enableTenantFilter = false)
    {
        var connectionString = $"Data Source=xihan-tasklog-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
        Client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings
            {
                IsAutoDeleteQueryFilter = true,
                IsAutoUpdateQueryFilter = true
            }
        });

        if (enableTenantFilter)
        {
            Client.QueryFilter.AddTableFilter<IMultiTenantEntity>(entity => entity.TenantId == 0 || entity.TenantId == ScopeTenantId);
            Client.QueryFilter.AddTableFilter<IStrictMultiTenantEntity>(entity => entity.TenantId == ScopeTenantId);
        }
    }

    /// <summary>
    /// 租户过滤使用的作用域租户；平台为 0
    /// </summary>
    public long ScopeTenantId { get; set; }

    /// <summary>
    /// 测试连接
    /// </summary>
    public SqlSugarClient Client { get; }

    /// <summary>
    /// 插入一条指定创建时间与租户的任务执行历史，写入对应月份的分表
    /// </summary>
    public void Insert(DateTimeOffset createdTime, long tenantId = 0)
    {
        var log = new SysTaskLog
        {
            TenantId = tenantId,
            TaskId = 1,
            TaskCode = "test-task",
            TaskName = "测试任务",
            StartTime = createdTime,
            CreatedTime = createdTime
        };
        typeof(SysTaskLog).GetProperty(nameof(SysTaskLog.BasicId))!.SetValue(log, _nextId++);
        Client.Insertable(log).SplitTable().ExecuteCommand();
    }

    /// <summary>
    /// 全部分表的总行数（不经租户过滤）
    /// </summary>
    public int CountAll()
    {
        return Client.Queryable<SysTaskLog>().ClearFilter().SplitTable().Count();
    }

    /// <summary>
    /// 全部分表中剩余行的创建时间（不经租户过滤）
    /// </summary>
    public List<DateTimeOffset> AllCreatedTimes()
    {
        return AllRows().Select(row => row.CreatedTime).ToList();
    }

    /// <summary>
    /// 全部分表中剩余行的租户与创建时间（不经租户过滤）
    /// </summary>
    public List<(long TenantId, DateTimeOffset CreatedTime)> AllRows()
    {
        return Client.Queryable<SysTaskLog>().ClearFilter().SplitTable().ToList()
            .Select(log => (log.TenantId, log.CreatedTime))
            .ToList();
    }

    /// <summary>
    /// 释放连接
    /// </summary>
    public void Dispose()
    {
        Client.Dispose();
        _keepAlive.Dispose();
    }
}
