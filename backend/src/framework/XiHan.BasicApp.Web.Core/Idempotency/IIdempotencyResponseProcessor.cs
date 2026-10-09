// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;

namespace XiHan.BasicApp.Web.Core.Idempotency;

/// <summary>
/// 幂等响应处理器
/// </summary>
/// <remarks>
/// 完成过滤器在保存响应快照之前，按注册顺序调用全部处理器就地处理 <see cref="Microsoft.AspNetCore.Mvc.ObjectResult"/> 的非空值；
/// 快照保存处理后的值，重播返回该快照。默认不注册任何处理器。处理器抛出的异常向外传播，完成不写入。
/// </remarks>
public interface IIdempotencyResponseProcessor
{
    /// <summary>
    /// 就地处理动作结果值
    /// </summary>
    /// <param name="httpContext">当前请求上下文</param>
    /// <param name="value">动作结果值</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task ProcessAsync(HttpContext httpContext, object value, CancellationToken cancellationToken = default);
}
