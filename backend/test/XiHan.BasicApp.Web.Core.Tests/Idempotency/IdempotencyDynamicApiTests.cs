// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Text;
using System.Text.Json;
using XiHan.BasicApp.Core.Attributes;
using XiHan.BasicApp.Web.Core.Idempotency;
using XiHan.Framework.Application.Contracts.Services;
using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Security.Users;
using XiHan.Framework.Uow;
using XiHan.Framework.Uow.Abstracts;
using XiHan.Framework.Uow.Attributes;
using XiHan.Framework.Uow.Options;
using XiHan.Framework.Web.Api.DynamicApi.Extensions;
using XiHan.Framework.Web.Api.DynamicApi.Helpers;
using XiHan.Framework.Web.Api.Filters;

namespace XiHan.BasicApp.Web.Core.Tests.Idempotency;

/// <summary>
/// 幂等测试用应用服务，经动态 API 投影为控制器
/// </summary>
public class IdempotencySampleAppService : IApplicationService
{
    private readonly ExecutionCounter _counter;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="counter">执行计数器</param>
    public IdempotencySampleAppService(ExecutionCounter counter)
    {
        _counter = counter;
    }

    /// <summary>
    /// 事务型幂等下单
    /// </summary>
    [Idempotent]
    [UnitOfWork(true)]
    public Task<OrderResult> CreateOrderAsync(CreateOrderInput input)
    {
        return Task.FromResult(new OrderResult(_counter.Increment(), input.Sku));
    }
}

/// <summary>
/// 动态 API 承载应用服务时的幂等集成测试
/// </summary>
public sealed class IdempotencyDynamicApiTests : IAsyncLifetime
{
    private readonly ExecutionCounter _counter = new();
    private IHost _host = null!;
    private HttpClient _client = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddOptions<XiHanUnitOfWorkDefaultOptions>();
                    services.AddSingleton<IAmbientUnitOfWork, AmbientUnitOfWork>();
                    services.AddSingleton<IUnitOfWorkManager, UnitOfWorkManager>();
                    services.AddSingleton<IUnitOfWorkEventPublisher, NullUnitOfWorkEventPublisher>();
                    services.AddSingleton<IUnitOfWorkTransactionBehaviourProvider, NullUnitOfWorkTransactionBehaviourProvider>();
                    services.AddTransient<IUnitOfWork, UnitOfWork>();
                    services.AddSingleton(_counter);
                    services.AddSingleton<ICurrentUser>(new FakeCurrentUser { IsAuthenticated = true, UserId = 7 });
                    services.AddSingleton<ICurrentTenant, FakeCurrentTenant>();
                    services.AddTransient<IdempotencySampleAppService>();
                    services.AddScoped<XiHanApiResponseResultFilter>();
                    services.AddScoped<XiHanUnitOfWorkFilter>();
                    services.AddDynamicApi();
                    services.AddControllers(options =>
                    {
                        options.Filters.AddService<XiHanApiResponseResultFilter>();
                        options.Filters.AddService<XiHanUnitOfWorkFilter>();
                    });
                    services.AddBasicAppIdempotency(new ConfigurationBuilder().Build());
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .StartAsync();
        _client = _host.GetTestClient();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>
    /// 应用服务方法上的幂等标注经动态 API 生效：同键重复请求重播首次响应，方法只执行一次
    /// </summary>
    [Fact]
    public async Task DynamicApi_IdempotentAppServiceMethod_ReplaysResponse()
    {
        var (httpMethod, path) = ResolveDynamicRoute();

        var first = await SendAsync(httpMethod, path, "dyn-key", "SKU-A");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(first.Headers.Contains(IdempotencyFilter.ReplayedHeaderName));
        var firstData = (await ReadDataAsync(first)).GetRawText();

        var replay = await SendAsync(httpMethod, path, "dyn-key", "SKU-A");

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.TryGetValues(IdempotencyFilter.ReplayedHeaderName, out var values) && values.Contains("true"));
        Assert.Equal(firstData, (await ReadDataAsync(replay)).GetRawText());
        Assert.Equal(1, _counter.Count);
    }

    /// <summary>
    /// 查找应用服务方法对应的动态控制器动作，返回其 HTTP 方法与路由
    /// </summary>
    private (HttpMethod Method, string Path) ResolveDynamicRoute()
    {
        var serviceMethod = typeof(IdempotencySampleAppService).GetMethod(nameof(IdempotencySampleAppService.CreateOrderAsync))!;
        var descriptor = _host.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Single(action => OriginalMethodResolver.Resolve(action.MethodInfo) == serviceMethod);

        Assert.NotEqual(typeof(IdempotencySampleAppService), descriptor.ControllerTypeInfo.AsType());
        var httpMethod = descriptor.ActionConstraints!.OfType<HttpMethodActionConstraint>().Single().HttpMethods.Single();
        return (new HttpMethod(httpMethod), "/" + descriptor.AttributeRouteInfo!.Template);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string key, string sku)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent($$"""{"sku":"{{sku}}","quantity":1}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
