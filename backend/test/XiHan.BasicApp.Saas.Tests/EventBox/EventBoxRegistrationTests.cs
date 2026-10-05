// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Extensions;
using XiHan.BasicApp.Saas.Infrastructure.EventBus;
using XiHan.Framework.Data.SqlSugar.Options;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.EventBus.Distributed;

namespace XiHan.BasicApp.Saas.Tests.EventBox;

/// <summary>
/// 收发件箱注册测试
/// </summary>
public sealed class EventBoxRegistrationTests
{
    /// <summary>
    /// 替换框架默认注册为作用域的 Saas 实现，且只保留一个
    /// </summary>
    [Fact]
    public void AddSaasEventBoxes_ReplacesDefaultsWithScopedSaasImplementations()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(IEventOutbox), typeof(object));
        services.AddSingleton(typeof(IEventInbox), typeof(object));

        services.AddSaasEventBoxes(BuildConfiguration([]));

        var outbox = Assert.Single(services, d => d.ServiceType == typeof(IEventOutbox));
        var inbox = Assert.Single(services, d => d.ServiceType == typeof(IEventInbox));
        Assert.Equal(ServiceLifetime.Scoped, outbox.Lifetime);
        Assert.Equal(typeof(SaasEventOutbox), outbox.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, inbox.Lifetime);
        Assert.Equal(typeof(SaasEventInbox), inbox.ImplementationType);
    }

    /// <summary>
    /// 分布式事件总线的默认收发件箱指向 Saas 实现
    /// </summary>
    [Fact]
    public void AddSaasEventBoxes_ConfiguresDistributedEventBusBoxes()
    {
        var services = new ServiceCollection();
        services.AddSaasEventBoxes(BuildConfiguration([]));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<XiHanDistributedEventBusOptions>>().Value;

        Assert.Equal(typeof(SaasEventOutbox), options.Outboxes["Default"].ImplementationType);
        Assert.Equal(typeof(SaasEventInbox), options.Inboxes["Default"].ImplementationType);
    }

    /// <summary>
    /// 绑定配置节
    /// </summary>
    [Fact]
    public void AddSaasEventBoxes_BindsConfigurationSection()
    {
        var services = new ServiceCollection();
        services.AddSaasEventBoxes(BuildConfiguration(new() { ["Saas:EventBus:Box:ClaimTimeout"] = "00:01:00" }));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromMinutes(1), provider.GetRequiredService<IOptions<SaasEventBoxOptions>>().Value.ClaimTimeout);
    }

    /// <summary>
    /// 领取超时不大于零时配置校验失败
    /// </summary>
    [Fact]
    public void AddSaasEventBoxes_RejectsNonPositiveClaimTimeout()
    {
        var services = new ServiceCollection();
        services.AddSaasEventBoxes(BuildConfiguration(new() { ["Saas:EventBus:Box:ClaimTimeout"] = "00:00:00" }));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SaasEventBoxOptions>>().Value);
    }

    /// <summary>
    /// 收件箱保留期不大于零时配置校验失败
    /// </summary>
    [Fact]
    public void AddSaasEventBoxes_RejectsNonPositiveInboxRetentionPeriod()
    {
        var services = new ServiceCollection();
        services.AddSaasEventBoxes(BuildConfiguration(new() { ["Saas:EventBus:Box:InboxRetentionPeriod"] = "00:00:00" }));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SaasEventBoxOptions>>().Value);
    }

    /// <summary>
    /// 连接配置钩子套用去重键列定义，并保留已注册的钩子
    /// </summary>
    [Fact]
    public void AddSaasEventBoxes_AppliesDedupKeyConventionAndKeepsExistingHook()
    {
        var services = new ServiceCollection();
        var existingHookCalled = false;
        services.Configure<XiHanSqlSugarCoreOptions>(options => options.ConfigureConnectionConfigs = _ => existingHookCalled = true);
        services.AddSaasEventBoxes(BuildConfiguration([]));
        using var provider = services.BuildServiceProvider();
        var config = new ConnectionConfig
        {
            ConfigId = $"registration-{Guid.NewGuid():N}",
            ConnectionString = "Server=127.0.0.1",
            DbType = DbType.MySql,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute
        };

        provider.GetRequiredService<IOptions<XiHanSqlSugarCoreOptions>>().Value.ConfigureConnectionConfigs!.Invoke([config]);

        Assert.True(existingHookCalled);
        using var client = new SqlSugarClient(config);
        var column = client.EntityMaintenance.GetEntityInfo<SysEventInbox>().Columns.Single(c => c.PropertyName == nameof(SysEventInbox.DedupKey));
        Assert.Equal("varchar(256) COLLATE utf8mb4_bin", column.DataType);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
