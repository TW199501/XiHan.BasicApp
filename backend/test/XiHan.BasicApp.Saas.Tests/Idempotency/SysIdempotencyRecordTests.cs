// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Data.Sqlite;
using SqlSugar;
using System.Reflection;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.Framework.Data.SqlSugar.Routing;

namespace XiHan.BasicApp.Saas.Tests.Idempotency;

/// <summary>
/// 接口幂等记录实体测试
/// </summary>
public class SysIdempotencyRecordTests : IDisposable
{
    private readonly string _connectionString = $"Data Source=xihan-idempotency-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;

    /// <summary>
    /// 打开保活连接，使共享内存库在测试期间存在
    /// </summary>
    public SysIdempotencyRecordTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    /// <summary>
    /// 释放保活连接，内存库随之销毁
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _keepAlive.Dispose();
    }

    /// <summary>
    /// 实体映射到平台库的接口幂等记录表
    /// </summary>
    [Fact]
    public void Entity_MapsToPlatformIdempotencyTable()
    {
        var client = CreateClient();

        Assert.Equal("Sys_Idempotency_Record", client.EntityMaintenance.GetTableName<SysIdempotencyRecord>());
        Assert.NotNull(typeof(SysIdempotencyRecord).GetCustomAttribute<PlatformDataSourceAttribute>());
    }

    /// <summary>
    /// 连续两次建表不抛出
    /// </summary>
    [Fact]
    public void InitTables_IsRepeatable()
    {
        var client = CreateClient();

        client.CodeFirst.InitTables(typeof(SysIdempotencyRecord));
        var exception = Record.Exception(() => client.CodeFirst.InitTables(typeof(SysIdempotencyRecord)));

        Assert.Null(exception);
    }

    /// <summary>
    /// 唯一索引拒绝重复的记录键摘要
    /// </summary>
    [Fact]
    public async Task UniqueIndex_RejectsDuplicateKeyHash()
    {
        var client = CreateClient();
        client.CodeFirst.InitTables(typeof(SysIdempotencyRecord));

        await client.Insertable(CreateRecord("hash-a")).ExecuteCommandAsync();
        await client.Insertable(CreateRecord("hash-b")).ExecuteCommandAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => client.Insertable(CreateRecord("hash-a")).ExecuteCommandAsync());
        Assert.Equal(2, await client.Queryable<SysIdempotencyRecord>().CountAsync());
    }

    private static SysIdempotencyRecord CreateRecord(string keyHash)
    {
        return new SysIdempotencyRecord(Guid.NewGuid())
        {
            KeyHash = keyHash,
            TenantId = string.Empty,
            SubjectId = "subject",
            HttpMethod = "POST",
            Endpoint = "/orders",
            IdempotencyKey = "key",
            Fingerprint = "fingerprint",
            Status = SysIdempotencyRecord.StatusProcessing,
            OwnerToken = Guid.NewGuid(),
            LeaseExpiresTime = DateTimeOffset.UtcNow.AddMinutes(1),
            CreatedTime = DateTimeOffset.UtcNow
        };
    }

    private SqlSugarClient CreateClient()
    {
        return new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        });
    }
}
