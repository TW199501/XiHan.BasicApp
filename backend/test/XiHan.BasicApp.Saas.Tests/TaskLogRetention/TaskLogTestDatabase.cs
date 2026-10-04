// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Data.Sqlite;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;

namespace XiHan.BasicApp.Saas.Tests.TaskLogRetention;

/// <summary>
/// 任务执行历史测试库：共享内存 SQLite，按需建立按月分表
/// </summary>
internal sealed class TaskLogTestDatabase : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private long _nextId = 1;

    /// <summary>
    /// 建库
    /// </summary>
    public TaskLogTestDatabase()
    {
        var connectionString = $"Data Source=xihan-tasklog-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
        Client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute
        });
    }

    /// <summary>
    /// 测试连接
    /// </summary>
    public SqlSugarClient Client { get; }

    /// <summary>
    /// 插入一条指定创建时间的任务执行历史，写入对应月份的分表
    /// </summary>
    public void Insert(DateTimeOffset createdTime)
    {
        var log = new SysTaskLog
        {
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
    /// 全部分表的总行数
    /// </summary>
    public int CountAll()
    {
        return Client.Queryable<SysTaskLog>().SplitTable().Count();
    }

    /// <summary>
    /// 全部分表中剩余行的创建时间
    /// </summary>
    public List<DateTimeOffset> AllCreatedTimes()
    {
        return Client.Queryable<SysTaskLog>().SplitTable().ToList().Select(log => log.CreatedTime).ToList();
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
