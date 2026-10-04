// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XiHan.BasicApp.Saas.Extensions;
using XiHan.BasicApp.Saas.Infrastructure.Idempotency;
using XiHan.BasicApp.Web.Core.Idempotency;

namespace XiHan.BasicApp.Saas.Tests.Idempotency;

/// <summary>
/// 过期幂等记录定期清理服务测试
/// </summary>
public class SaasIdempotencyPurgeHostedServiceTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 按间隔反复清理，每次在新的作用域中进行；某次失败不影响后续清理
    /// </summary>
    [Fact]
    public async Task 按间隔清理且单次失败不中断()
    {
        var purger = new FakePurger(failOnCall: 1, signalAtCall: 3);
        using var provider = BuildProvider(purger, TimeSpan.FromMilliseconds(50));
        var service = CreateService(provider);

        await service.StartAsync(CancellationToken.None);
        await purger.Signal.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.True(purger.Calls >= 3);
        Assert.True(purger.Scopes.Count >= 3);
        Assert.Equal(purger.Scopes.Count, purger.Scopes.Distinct().Count());
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    /// <summary>
    /// 启动后首次清理发生在一个间隔之后
    /// </summary>
    [Fact]
    public async Task 启动时不立即清理()
    {
        var purger = new FakePurger(failOnCall: 0, signalAtCall: 1);
        using var provider = BuildProvider(purger, TimeSpan.FromHours(1));
        var service = CreateService(provider);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, purger.Calls);
        Assert.True(service.ExecuteTask!.IsCompleted);
        Assert.False(service.ExecuteTask.IsFaulted);
    }

    /// <summary>
    /// 停止时取消进行中的清理并结束循环
    /// </summary>
    [Fact]
    public async Task 停止时取消进行中的清理()
    {
        var purger = new FakePurger(failOnCall: 0, signalAtCall: 1) { BlockUntilCanceled = true };
        using var provider = BuildProvider(purger, TimeSpan.FromMilliseconds(50));
        var service = CreateService(provider);

        await service.StartAsync(CancellationToken.None);
        await purger.Signal.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.True(purger.ObservedCancellation);
        Assert.True(service.ExecuteTask!.IsCompleted);
        Assert.False(service.ExecuteTask.IsFaulted);
    }

    /// <summary>
    /// 注册清理服务与清理入口
    /// </summary>
    [Fact]
    public void 注册清理服务与清理入口()
    {
        var services = new ServiceCollection();

        services.AddSaasIdempotencyStore(new ConfigurationBuilder().Build());

        Assert.Contains(services, service => service.ServiceType == typeof(IHostedService) &&
                                             service.ImplementationType == typeof(SaasIdempotencyPurgeHostedService));
        var purger = Assert.Single(services, service => service.ServiceType == typeof(IIdempotencyRecordPurger));
        Assert.Equal(ServiceLifetime.Scoped, purger.Lifetime);
    }

    /// <summary>
    /// 清理间隔默认一小时
    /// </summary>
    [Fact]
    public void 清理间隔默认一小时()
    {
        Assert.Equal(TimeSpan.FromHours(1), new IdempotencyOptions().PurgeInterval);
    }

    /// <summary>
    /// 清理间隔不大于零时启动校验失败
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 清理间隔不大于零时启动校验失败(int seconds)
    {
        var services = new ServiceCollection();
        services.AddOptions<IdempotencyOptions>().Configure(options => options.PurgeInterval = TimeSpan.FromSeconds(seconds));
        services.AddSaasIdempotencyStore(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(provider.GetRequiredService<IStartupValidator>().Validate);
    }

    private static ServiceProvider BuildProvider(FakePurger purger, TimeSpan interval)
    {
        var services = new ServiceCollection();
        services.AddOptions<IdempotencyOptions>().Configure(options => options.PurgeInterval = interval);
        services.AddScoped<IIdempotencyRecordPurger>(scoped => new ScopedPurger(purger, scoped));
        return services.BuildServiceProvider();
    }

    private static SaasIdempotencyPurgeHostedService CreateService(ServiceProvider provider)
    {
        return new SaasIdempotencyPurgeHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<IdempotencyOptions>>(),
            TimeProvider.System,
            NullLogger<SaasIdempotencyPurgeHostedService>.Instance);
    }

    /// <summary>
    /// 记录调用的清理替身
    /// </summary>
    private sealed class FakePurger(int failOnCall, int signalAtCall)
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool BlockUntilCanceled { get; init; }

        public bool ObservedCancellation { get; private set; }

        public System.Collections.Concurrent.ConcurrentQueue<IServiceProvider> Scopes { get; } = new();

        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<int> PurgeAsync(IServiceProvider scope, CancellationToken cancellationToken)
        {
            Scopes.Enqueue(scope);
            var call = Interlocked.Increment(ref _calls);
            if (call >= signalAtCall)
            {
                Signal.TrySetResult();
            }

            if (BlockUntilCanceled)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    ObservedCancellation = true;
                    throw;
                }
            }

            if (call == failOnCall)
            {
                throw new InvalidOperationException("清理失败");
            }

            return 1;
        }
    }

    /// <summary>
    /// 绑定到作用域的清理入口
    /// </summary>
    private sealed class ScopedPurger(FakePurger purger, IServiceProvider scope) : IIdempotencyRecordPurger
    {
        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
        {
            return purger.PurgeAsync(scope, cancellationToken);
        }
    }
}
