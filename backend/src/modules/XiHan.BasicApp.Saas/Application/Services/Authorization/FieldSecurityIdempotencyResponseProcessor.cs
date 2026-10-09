// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;
using XiHan.BasicApp.Web.Core.Idempotency;

namespace XiHan.BasicApp.Saas.Application.Services;

/// <summary>
/// 幂等快照的字段安全处理器：保存快照前按当前用户的规则就地打码，并标记该值已打码
/// </summary>
/// <remarks>
/// 标记后 <see cref="FieldSecurityResponseFilter"/> 对同一实例不再打码，首次响应只打码一次，与快照一致。
/// </remarks>
public sealed class FieldSecurityIdempotencyResponseProcessor(IFieldSecurityService fieldSecurity) : IIdempotencyResponseProcessor
{
    /// <inheritdoc />
    public async Task ProcessAsync(HttpContext httpContext, object value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(value);

        await fieldSecurity.MaskAsync(value, cancellationToken);
        FieldSecurityResponseFilter.MarkMasked(httpContext, value);
    }
}
