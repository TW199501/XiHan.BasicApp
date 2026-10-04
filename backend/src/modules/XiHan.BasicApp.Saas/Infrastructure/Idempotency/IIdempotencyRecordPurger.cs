// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.BasicApp.Saas.Infrastructure.Idempotency;

/// <summary>
/// 过期幂等记录清理入口
/// </summary>
public interface IIdempotencyRecordPurger
{
    /// <summary>
    /// 删除已过期的完成记录与不确定记录
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>删除的记录数</returns>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
