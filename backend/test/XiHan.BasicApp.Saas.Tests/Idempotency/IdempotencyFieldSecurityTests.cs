// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;
using System.Reflection;
using System.Text.Json;
using XiHan.BasicApp.Core.Attributes;
using XiHan.BasicApp.Saas.Application.Services;
using XiHan.BasicApp.Saas.Domain.DomainServices;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Domain.Enums;
using XiHan.BasicApp.Saas.Domain.Repositories;
using XiHan.BasicApp.Saas.Extensions;
using XiHan.BasicApp.Web.Core.Idempotency;
using XiHan.Framework.MultiTenancy.Abstractions;
using XiHan.Framework.Security.Users;
using XiHan.Framework.Uow;
using XiHan.Framework.Uow.Abstracts;
using XiHan.Framework.Uow.Options;

namespace XiHan.BasicApp.Saas.Tests.Idempotency;

/// <summary>
/// 接口幂等与字段安全输出边界的组合测试：真实字段安全过滤器、幂等外层与完成过滤器串成一条链
/// </summary>
public sealed class IdempotencyFieldSecurityTests
{
    private const long UserId = 1;

    private const string RawPhone = "13812345678";

    private const string RawEmail = "alice@example.com";

    /// <summary>
    /// 首次响应按当前用户打码一次；重播响应与首次相同；快照不含明文
    /// </summary>
    [Fact]
    public async Task 重播响应与首次打码响应一致且快照不含明文()
    {
        using var provider = BuildProvider();
        var invocations = 0;
        Func<object> action = () =>
        {
            invocations++;
            return new UserRow { UserName = "alice", Phone = RawPhone, Email = RawEmail };
        };

        var first = await RunAsync(provider, action);
        var second = await RunAsync(provider, action);

        Assert.Equal(1, invocations);
        var firstValue = Assert.IsType<UserRow>(first.Result.Value);
        Assert.Equal(FieldMasker.Mask(RawPhone, HashRule), firstValue.Phone);
        Assert.Equal(new string('*', RawEmail.Length), firstValue.Email);
        Assert.Equal("alice", firstValue.UserName);

        Assert.True(second.Replayed);
        var replayBody = Assert.IsType<JsonElement>(second.Result.Value).GetRawText();
        Assert.Equal(Serialize(firstValue), replayBody);
        Assert.DoesNotContain(RawPhone, replayBody, StringComparison.Ordinal);
        Assert.DoesNotContain(RawEmail, replayBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// 字段安全过滤器只跳过标记为已打码的同一实例，其他结果值照常打码
    /// </summary>
    [Fact]
    public async Task 字段安全过滤器只跳过已标记的同一实例()
    {
        using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ControllerActionDescriptor());
        var executing = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: new object());
        var filter = new FieldSecurityResponseFilter(scope.ServiceProvider.GetRequiredService<IFieldSecurityService>());
        FieldSecurityResponseFilter.MarkMasked(httpContext, new UserRow { Phone = RawPhone });
        var other = new UserRow { Phone = RawPhone };

        await filter.OnActionExecutionAsync(executing, () =>
            Task.FromResult(new ActionExecutedContext(actionContext, [], executing.Controller) { Result = new ObjectResult(other) }));

        Assert.Equal(FieldMasker.Mask(RawPhone, HashRule), other.Phone);
    }

    private static EffectiveFieldRule HashRule { get; } = new()
    {
        FieldName = nameof(SysUser.Phone),
        MaskStrategy = FieldMaskStrategy.Hash
    };

    private static string Serialize(object value)
    {
        return JsonSerializer.Serialize(value, value.GetType(), new JsonOptions().JsonSerializerOptions);
    }

    /// <summary>
    /// 按 MVC 的过滤器调用方式串起：字段安全过滤器 → 幂等外层过滤器 → 幂等完成过滤器 → 动作
    /// </summary>
    private static async Task<ChainResult> RunAsync(ServiceProvider provider, Func<object> action)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var method = typeof(SampleUserAppService).GetMethod(nameof(SampleUserAppService.CreateUser))!;
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/api/SampleUser";
        httpContext.Request.Headers["Idempotency-Key"] = "k-1";
        var descriptor = new ControllerActionDescriptor
        {
            MethodInfo = method,
            ControllerTypeInfo = typeof(SampleUserAppService).GetTypeInfo(),
            ActionName = method.Name,
            ControllerName = nameof(SampleUserAppService)
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        var executing = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: new object());

        IAsyncActionFilter[] filters =
        [
            new FieldSecurityResponseFilter(services.GetRequiredService<IFieldSecurityService>()),
            services.GetRequiredService<IdempotencyFilter>(),
            services.GetRequiredService<IdempotencyCompletionFilter>()
        ];
        var executed = await InvokeAsync(filters, 0, executing, actionContext, action);

        return new ChainResult(
            Assert.IsType<ObjectResult>(executed.Result),
            httpContext.Response.Headers[IdempotencyFilter.ReplayedHeaderName] == "true");
    }

    private static async Task<ActionExecutedContext> InvokeAsync(
        IAsyncActionFilter[] filters, int index, ActionExecutingContext executing, ActionContext actionContext, Func<object> action)
    {
        if (index == filters.Length)
        {
            return new ActionExecutedContext(actionContext, [], executing.Controller) { Result = new ObjectResult(action()) };
        }

        ActionExecutedContext? executed = null;
        await filters[index].OnActionExecutionAsync(executing, async () =>
        {
            executed = await InvokeAsync(filters, index + 1, executing, actionContext, action);
            return executed;
        });

        return executed ?? new ActionExecutedContext(actionContext, [], executing.Controller) { Canceled = true, Result = executing.Result };
    }

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<XiHanUnitOfWorkDefaultOptions>();
        services.AddSingleton<IAmbientUnitOfWork, AmbientUnitOfWork>();
        services.AddSingleton<IUnitOfWorkManager, UnitOfWorkManager>();
        services.AddSingleton<IUnitOfWorkEventPublisher, NullUnitOfWorkEventPublisher>();
        services.AddSingleton<IUnitOfWorkTransactionBehaviourProvider, NullUnitOfWorkTransactionBehaviourProvider>();
        services.AddTransient<IUnitOfWork, UnitOfWork>();

        services.AddBasicAppIdempotency(configuration);
        services.AddSaasIdempotencyStore(configuration);
        services.Replace(ServiceDescriptor.Singleton<IIdempotencyStore, DefaultIdempotencyStore>());

        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(user => user.IsAuthenticated).Returns(true);
        currentUser.SetupGet(user => user.UserId).Returns(UserId);
        services.AddSingleton(currentUser.Object);
        services.AddSingleton<ICurrentTenant>(new TestCurrentTenant());
        services.AddScoped<IFieldSecurityService>(_ => CreateFieldSecurityService(currentUser.Object));
        return services.BuildServiceProvider();
    }

    private static FieldSecurityService CreateFieldSecurityService(ICurrentUser currentUser)
    {
        var rules = new Mock<IFieldLevelSecurityRepository>();
        rules.Setup(repo => repo.GetEnabledByEntityAsync(nameof(SysUser), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                UserRule(nameof(SysUser.Phone), FieldMaskStrategy.Hash, 1),
                UserRule(nameof(SysUser.Email), FieldMaskStrategy.FullMask, 2)
            ]);
        var userRoles = new Mock<IUserRoleRepository>();
        userRoles.Setup(repo => repo.GetValidByUserIdAsync(It.IsAny<long>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var userDepartments = new Mock<IUserDepartmentRepository>();
        userDepartments.Setup(repo => repo.GetValidByUserIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var dtoCatalog = new Mock<IFieldSecurityDtoCatalog>();
        dtoCatalog.Setup(catalog => catalog.EntityOf(It.IsAny<Type>())).Returns((Type type) => type == typeof(UserRow) ? typeof(SysUser) : null);

        return new FieldSecurityService(
            new FieldSecurityEntityCatalog(Options.Create(new FieldSecurityEntityOptions().Add<SysUser>())),
            dtoCatalog.Object,
            rules.Object,
            new Mock<IFieldSecurityEntityReader>().Object,
            userRoles.Object,
            new Mock<IRoleRepository>().Object,
            userDepartments.Object,
            new Mock<IDepartmentRepository>().Object,
            currentUser);
    }

    private static SysFieldLevelSecurity UserRule(string fieldName, FieldMaskStrategy strategy, long id)
    {
        var rule = new SysFieldLevelSecurity
        {
            TargetType = FieldSecurityTargetType.User,
            TargetId = UserId,
            EntityName = nameof(SysUser),
            FieldName = fieldName,
            MaskStrategy = strategy,
            IsEditable = false,
            Status = EnableStatus.Enabled
        };
        SaasTestHelper.SetBasicId(rule, id);
        return rule;
    }

    private sealed record ChainResult(ObjectResult Result, bool Replayed);

    /// <summary>
    /// 与 SysUser 同名属性的返回对象
    /// </summary>
    private sealed class UserRow
    {
        public string UserName { get; set; } = string.Empty;

        public string? Phone { get; set; }

        public string? Email { get; set; }
    }

    /// <summary>
    /// 标注幂等的示例应用服务
    /// </summary>
    private sealed class SampleUserAppService
    {
        [Idempotent]
        public object CreateUser()
        {
            return new object();
        }
    }
}
