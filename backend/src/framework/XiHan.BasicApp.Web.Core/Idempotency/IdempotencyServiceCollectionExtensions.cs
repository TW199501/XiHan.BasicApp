// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XiHan.Framework.Web.Api.Filters;

namespace XiHan.BasicApp.Web.Core.Idempotency;

/// <summary>
/// 接口幂等服务集合扩展
/// </summary>
public static class IdempotencyServiceCollectionExtensions
{
    /// <summary>
    /// 添加接口幂等保护：配置、默认进程内存储，以及插在工作单元过滤器前后的两层过滤器
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddBasicAppIdempotency(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<IdempotencyOptions>()
            .Bind(configuration.GetSection(IdempotencyOptions.SectionName))
            .Validate(options => options.MaxKeyLength > 0 && options.MaxRequestBytes > 0 && options.MaxResponseBytes > 0 &&
                                 options.MaxEntries > 0 && options.MaxTotalResponseBytes > 0 &&
                                 options.CompletedRetention > TimeSpan.Zero && options.ProcessingLease > TimeSpan.Zero,
                "幂等配置无效：各上限与时长必须大于零。")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IIdempotencyStore, DefaultIdempotencyStore>();
        services.TryAddScoped<IdempotencyFilter>();
        services.TryAddScoped<IdempotencyCompletionFilter>();
        services.PostConfigure<MvcOptions>(InsertFilters);

        return services;
    }

    /// <summary>
    /// 把幂等外层过滤器插在工作单元过滤器之前，完成过滤器插在其之后
    /// </summary>
    /// <param name="options">MVC 配置</param>
    /// <exception cref="InvalidOperationException">未找到工作单元过滤器</exception>
    public static void InsertFilters(MvcOptions options)
    {
        if (options.Filters.Any(filter => filter is ServiceFilterAttribute { ServiceType: var type } && type == typeof(IdempotencyFilter)))
        {
            return;
        }

        var index = -1;
        for (var i = 0; i < options.Filters.Count; i++)
        {
            if (options.Filters[i] is ServiceFilterAttribute { ServiceType: var type } && type == typeof(XiHanUnitOfWorkFilter))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            throw new InvalidOperationException("未找到工作单元过滤器 XiHanUnitOfWorkFilter，无法注册接口幂等过滤器。");
        }

        options.Filters.Insert(index + 1, new ServiceFilterAttribute(typeof(IdempotencyCompletionFilter)));
        options.Filters.Insert(index, new ServiceFilterAttribute(typeof(IdempotencyFilter)));
    }
}
