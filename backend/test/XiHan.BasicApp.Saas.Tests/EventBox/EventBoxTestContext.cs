// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.EventBus;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.BasicApp.Saas.Tests.EventBox;

/// <summary>
/// 事件收发件箱测试上下文：临时 SQLite 库、可切换租户与布局的解析器、手动时钟
/// </summary>
internal sealed class EventBoxTestContext : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"xihan-eventbox-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 建库并建立收发件箱表
    /// </summary>
    public EventBoxTestContext()
    {
        Client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_databasePath};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        Client.CodeFirst.InitTables<SysEventOutbox, SysEventInbox>();

        Tenant = new FakeCurrentTenant();
        Resolver = new TestClientResolver(Client, Tenant);
        Clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// 测试连接
    /// </summary>
    public SqlSugarClient Client { get; }

    /// <summary>
    /// 当前租户替身
    /// </summary>
    public FakeCurrentTenant Tenant { get; }

    /// <summary>
    /// 客户端解析器替身
    /// </summary>
    public TestClientResolver Resolver { get; }

    /// <summary>
    /// 手动时钟
    /// </summary>
    public ManualTimeProvider Clock { get; }

    /// <summary>
    /// 建立接到测试库的发件箱
    /// </summary>
    public SaasEventOutbox CreateOutbox(SaasEventBoxOptions? options = null)
    {
        return new SaasEventOutbox(Resolver, Tenant, Options.Create(options ?? new SaasEventBoxOptions()), Clock);
    }

    /// <summary>
    /// 建立接到测试库的收件箱
    /// </summary>
    public SaasEventInbox CreateInbox(SaasEventBoxOptions? options = null)
    {
        return new SaasEventInbox(Resolver, Tenant, Options.Create(options ?? new SaasEventBoxOptions()), NullLogger<SaasEventInbox>.Instance, Clock);
    }

    /// <summary>
    /// 释放连接并删除临时库文件
    /// </summary>
    public void Dispose()
    {
        Client.Dispose();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}

/// <summary>
/// 可切换的当前租户替身
/// </summary>
internal sealed class FakeCurrentTenant : ICurrentTenant
{
    /// <summary>
    /// 是否处于租户上下文
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            return Id.HasValue;
        }
    }

    /// <summary>
    /// 租户标识
    /// </summary>
    public long? Id { get; private set; }

    /// <summary>
    /// 租户名称
    /// </summary>
    public string? Name { get; private set; }

    /// <summary>
    /// 切换租户，释放时恢复原值
    /// </summary>
    public IDisposable Change(long? id, string? name = null)
    {
        var previousId = Id;
        var previousName = Name;
        Id = id;
        Name = name;

        return new Restorer(() =>
        {
            Id = previousId;
            Name = previousName;
        });
    }

    /// <summary>
    /// 释放时执行恢复动作
    /// </summary>
    private sealed class Restorer(Action restore) : IDisposable
    {
        /// <summary>
        /// 恢复原租户
        /// </summary>
        public void Dispose()
        {
            restore();
        }
    }
}

/// <summary>
/// 固定返回单一测试连接、按租户返回布局的解析器替身
/// </summary>
internal sealed class TestClientResolver(ISqlSugarClient client, FakeCurrentTenant tenant) : ISqlSugarClientResolver
{
    /// <summary>
    /// 使用独立库布局的租户
    /// </summary>
    public HashSet<long> IndependentTenantIds { get; } = [];

    /// <summary>
    /// 获取当前客户端
    /// </summary>
    public ISqlSugarClient GetCurrentClient()
    {
        return client;
    }

    /// <summary>
    /// 获取实体对应的客户端
    /// </summary>
    public ISqlSugarClient GetClientForEntity(Type entityType)
    {
        return client;
    }

    /// <summary>
    /// 按配置标识获取客户端
    /// </summary>
    public ISqlSugarClient GetClient(string configId)
    {
        return client;
    }

    /// <summary>
    /// 获取全部配置标识
    /// </summary>
    public IReadOnlyCollection<string> GetAllConfigIds()
    {
        return ["Default"];
    }

    /// <summary>
    /// 独立库租户返回 Tenant_{id}，其余返回 Default
    /// </summary>
    public IReadOnlyList<string> GetCurrentLayoutConfigIds()
    {
        if (tenant.Id is { } id && IndependentTenantIds.Contains(id))
        {
            return [$"Tenant_{id}"];
        }

        return ["Default"];
    }

    /// <summary>
    /// 获取全部客户端
    /// </summary>
    public IEnumerable<ISqlSugarClient> GetAllClients()
    {
        return [client];
    }

    /// <summary>
    /// 不支持
    /// </summary>
    public ITenant AsTenant()
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 手动推进的时钟
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    /// <summary>
    /// 当前 UTC 时间
    /// </summary>
    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }

    /// <summary>
    /// 推进时钟
    /// </summary>
    public void Advance(TimeSpan delta)
    {
        _now += delta;
    }
}
