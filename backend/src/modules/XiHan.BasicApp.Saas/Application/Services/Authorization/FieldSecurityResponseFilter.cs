// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace XiHan.BasicApp.Saas.Application.Services;

/// <summary>
/// 字段安全的输出边界：每个接口返回前，按当前用户的规则给响应里登记实体的 DTO 打码
/// </summary>
/// <remarks>
/// 读脱敏放在这里统一做，不逐个接口接线：详情、选项、聚合详情、新建与修改后回传的详情都经过同一道口子，
/// 新接口不会因为漏接而漏出明文。进程内直调查询服务（如导出）不经过本过滤器，由调用方显式打码。
///
/// 必须排在响应缓存过滤器外层：缓存里存原值，每次命中按当次用户打码；排进内层会把第一个人看到的打码结果缓存给所有人。
/// HybridCache 对可变类型每次命中都给新反序列化的实例，就地打码不会污染缓存。
///
/// 经 <see cref="MarkMasked"/> 标记为已打码的结果值（如幂等快照保存前已打码的值）不再重复打码。
/// </remarks>
public sealed class FieldSecurityResponseFilter(IFieldSecurityService fieldSecurity) : IAsyncActionFilter
{
    /// <summary>
    /// 过滤器顺序：越小越靠外，须小于框架缓存、工作单元等动作过滤器（默认 0）
    /// </summary>
    public const int FilterOrder = -10_000;

    private static readonly object MaskedValueItemKey = new();

    /// <summary>
    /// 把本次请求的一个结果值标记为已打码，过滤器对同一实例不再打码
    /// </summary>
    /// <param name="httpContext">当前请求上下文</param>
    /// <param name="value">已打码的结果值</param>
    public static void MarkMasked(HttpContext httpContext, object value)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(value);

        httpContext.Items[MaskedValueItemKey] = value;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var executed = await next();
        if (executed.Exception is null && executed.Result is ObjectResult { Value: { } value } &&
            !ReferenceEquals(context.HttpContext.Items[MaskedValueItemKey], value))
        {
            await fieldSecurity.MaskAsync(value, context.HttpContext.RequestAborted);
        }
    }
}
