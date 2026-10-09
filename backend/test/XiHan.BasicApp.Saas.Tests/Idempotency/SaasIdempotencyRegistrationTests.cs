// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Reflection;
using XiHan.BasicApp.Saas.Extensions;
using XiHan.BasicApp.Saas.Infrastructure.Idempotency;
using XiHan.BasicApp.Web.Core;
using XiHan.BasicApp.Web.Core.Idempotency;
using XiHan.Framework.Core.Modularity;

namespace XiHan.BasicApp.Saas.Tests.Idempotency;

/// <summary>
/// Saas 幂等存储注册测试
/// </summary>
public class SaasIdempotencyRegistrationTests
{
    /// <summary>
    /// 注册后以作用域生命周期的 Saas 存储替换默认存储
    /// </summary>
    [Fact]
    public void 注册后替换默认存储为作用域Saas存储()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIdempotencyStore, DefaultIdempotencyStore>();

        services.AddSaasIdempotencyStore(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IIdempotencyStore));
        Assert.Equal(typeof(SaasIdempotencyStore), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Contains(services, service => service.ServiceType == typeof(TimeProvider));
    }

    /// <summary>
    /// 幂等键最大长度超过记录列长度时启动校验失败
    /// </summary>
    [Fact]
    public void 幂等键最大长度超过128时启动校验失败()
    {
        var services = new ServiceCollection();
        services.AddOptions<IdempotencyOptions>().Configure(options => options.MaxKeyLength = 129);
        services.AddSaasIdempotencyStore(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        var validator = provider.GetService<IStartupValidator>();

        Assert.NotNull(validator);
        Assert.Throws<OptionsValidationException>(validator.Validate);
    }

    /// <summary>
    /// 幂等键最大长度为 128 时启动校验通过
    /// </summary>
    [Fact]
    public void 幂等键最大长度为128时启动校验通过()
    {
        var services = new ServiceCollection();
        services.AddOptions<IdempotencyOptions>().Configure(options => options.MaxKeyLength = 128);
        services.AddSaasIdempotencyStore(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    /// <summary>
    /// 记录列长度常量为 128
    /// </summary>
    [Fact]
    public void 幂等键列长度常量为128()
    {
        Assert.Equal(128, SaasIdempotencyStore.MaxKeyColumnLength);
    }

    /// <summary>
    /// Saas 模块依赖提供幂等过滤器与默认存储的 Web.Core 模块
    /// </summary>
    [Fact]
    public void Saas模块依赖WebCore模块()
    {
        var dependsOn = typeof(XiHanBasicAppSaasModule).GetCustomAttribute<DependsOnAttribute>();

        Assert.NotNull(dependsOn);
        Assert.Contains(typeof(XiHanBasicAppWebCoreModule), dependsOn.DependedTypes);
    }
}
