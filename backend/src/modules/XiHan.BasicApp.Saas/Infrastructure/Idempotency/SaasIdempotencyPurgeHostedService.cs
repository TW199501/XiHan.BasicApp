// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XiHan.BasicApp.Web.Core.Idempotency;

namespace XiHan.BasicApp.Saas.Infrastructure.Idempotency;

/// <summary>
/// 过期幂等记录定期清理服务
/// </summary>
/// <remarks>
/// 启动后每隔 <see cref="IdempotencyOptions.PurgeInterval"/> 在新的作用域中调用一次 <see cref="IIdempotencyRecordPurger.PurgeExpiredAsync"/>；
/// 单次清理失败记录错误日志后继续下一次，停止时取消进行中的清理并结束。
/// </remarks>
public sealed class SaasIdempotencyPurgeHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<IdempotencyOptions> options,
    TimeProvider timeProvider,
    ILogger<SaasIdempotencyPurgeHostedService> logger) : BackgroundService
{
    /// <summary>
    /// 按间隔循环清理，直到停止
    /// </summary>
    /// <param name="stoppingToken">停止令牌</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.PurgeInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PurgeOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PurgeOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var purger = scope.ServiceProvider.GetRequiredService<IIdempotencyRecordPurger>();
            var deleted = await purger.PurgeExpiredAsync(stoppingToken);
            if (deleted > 0)
            {
                logger.LogInformation("已清理 {Count} 条过期幂等记录", deleted);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "清理过期幂等记录失败");
        }
    }
}
