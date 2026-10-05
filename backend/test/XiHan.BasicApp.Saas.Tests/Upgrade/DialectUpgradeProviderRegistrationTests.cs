// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XiHan.BasicApp.Saas.Extensions;
using XiHan.BasicApp.Saas.Infrastructure.Upgrade;
using XiHan.Framework.Upgrade.Abstractions;
using XiHan.Framework.Upgrade.Extensions;

namespace XiHan.BasicApp.Saas.Tests.Upgrade;

/// <summary>
/// 方言提供者的注册
/// </summary>
public sealed class DialectUpgradeProviderRegistrationTests
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    [Fact]
    public void 替换框架的文件系统提供者_只留一个作用域方言提供者()
    {
        var services = new ServiceCollection();
        services.AddXiHanUpgrade(EmptyConfiguration);

        services.AddSaasDomainServices();

        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IUpgradeScriptProvider));
        Assert.Equal(typeof(DialectAwareUpgradeScriptProvider), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void 先注册Saas再由框架TryAdd_仍只有方言提供者()
    {
        var services = new ServiceCollection();

        services.AddSaasDomainServices();
        services.AddXiHanUpgrade(EmptyConfiguration);

        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IUpgradeScriptProvider));
        Assert.Equal(typeof(DialectAwareUpgradeScriptProvider), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }
}
