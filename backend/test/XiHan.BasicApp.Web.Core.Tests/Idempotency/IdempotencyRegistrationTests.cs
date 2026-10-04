// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XiHan.BasicApp.Web.Core.Idempotency;
using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Security.Users;
using XiHan.Framework.Uow;
using XiHan.Framework.Uow.Abstracts;
using XiHan.Framework.Web.Api.Extensions.DependencyInjection;
using XiHan.Framework.Web.Api.Filters;

namespace XiHan.BasicApp.Web.Core.Tests.Idempotency;

/// <summary>
/// 幂等服务注册与过滤器顺序测试
/// </summary>
public class IdempotencyRegistrationTests
{
    [Fact]
    public void AddBasicAppIdempotency_RegistersDefaultsAndBindsSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BasicApp:Web:Idempotency:MaxKeyLength"] = "64" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUser, FakeCurrentUser>();
        services.AddSingleton<ICurrentTenant, FakeCurrentTenant>();
        services.AddSingleton<IAmbientUnitOfWork, AmbientUnitOfWork>();
        services.AddSingleton<IUnitOfWorkManager, UnitOfWorkManager>();
        services.AddBasicAppIdempotency(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.Equal(64, provider.GetRequiredService<IOptions<IdempotencyOptions>>().Value.MaxKeyLength);
        var store = provider.GetRequiredService<IIdempotencyStore>();
        Assert.IsType<DefaultIdempotencyStore>(store);
        Assert.Same(store, provider.GetRequiredService<IIdempotencyStore>());
        Assert.NotNull(scope.ServiceProvider.GetService<IdempotencyFilter>());
        Assert.NotNull(scope.ServiceProvider.GetService<IdempotencyCompletionFilter>());
    }

    [Fact]
    public void AddBasicAppIdempotency_KeepsReplacedStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIdempotencyStore, FakeStore>();
        services.AddBasicAppIdempotency(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        Assert.IsType<FakeStore>(provider.GetRequiredService<IIdempotencyStore>());
    }

    [Fact]
    public void AddXiHanWebApiMvcThenAddBasicAppIdempotency_OrdersIdempotencyFiltersAroundUnitOfWork()
    {
        var services = new ServiceCollection();
        services.AddXiHanWebApiMvc();
        services.AddBasicAppIdempotency(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters
            .OfType<ServiceFilterAttribute>()
            .Select(filter => filter.ServiceType)
            .ToList();

        var start = filters.IndexOf(typeof(XiHanCacheFilter));
        Assert.True(start >= 0);
        Assert.Equal(
            [typeof(XiHanCacheFilter), typeof(IdempotencyFilter), typeof(XiHanUnitOfWorkFilter), typeof(IdempotencyCompletionFilter)],
            filters.Skip(start).Take(4));
    }

    private sealed class FakeStore : IIdempotencyStore
    {
        public Task<IdempotencyAcquireResult> TryAcquireAsync(IdempotencyRecordKey key, string fingerprint, bool isTransactional, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CompleteAsync(IdempotencyRecordKey key, Guid ownerToken, StoredResponse response, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ReleaseAsync(IdempotencyRecordKey key, Guid ownerToken, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task MarkIndeterminateAsync(IdempotencyRecordKey key, Guid ownerToken, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
