// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.BasicApp.Saas.Infrastructure.EventBus;

/// <summary>
/// 发件箱的 SqlSugar 实现
/// </summary>
/// <remarks>
/// 事件行只存平台主库。入箱写当前客户端，与当前事务同库；领取与删除脱离租户上下文在平台主库执行。
/// 当前租户使用独立于平台的数据库布局时拒绝入箱。
/// </remarks>
public class SaasEventOutbox : IEventOutbox
{
    private const int MaxClaimAttempts = 3;

    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly ICurrentTenant _currentTenant;
    private readonly SaasEventBoxOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="currentTenant">当前租户</param>
    /// <param name="options">收发件箱存储配置</param>
    /// <param name="timeProvider">时钟，为 null 时使用系统时钟</param>
    public SaasEventOutbox(
        ISqlSugarClientResolver clientResolver,
        ICurrentTenant currentTenant,
        IOptions<SaasEventBoxOptions> options,
        TimeProvider? timeProvider = null)
    {
        _clientResolver = clientResolver;
        _currentTenant = currentTenant;
        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 将事件信息添加到发件箱
    /// </summary>
    /// <param name="outgoingEvent">出站事件信息</param>
    /// <exception cref="InvalidOperationException">当前租户使用独立于平台的数据库布局</exception>
    public async Task EnqueueAsync(OutgoingEventInfo outgoingEvent)
    {
        ArgumentNullException.ThrowIfNull(outgoingEvent);

        EnsureSharedLayout();

        var client = _clientResolver.GetCurrentClient();
        await client.Insertable(EventOutboxMapper.ToEntity(outgoingEvent)).ExecuteCommandAsync();
    }

    /// <summary>
    /// 领取一批待发送的事件信息
    /// </summary>
    /// <remarks>
    /// 返回前把记录标记为已领取；超过 <see cref="SaasEventBoxOptions.ClaimTimeout"/> 的已领取记录可被重新领取。
    /// </remarks>
    /// <param name="maxCount">最大数量</param>
    /// <param name="filter">过滤条件，本实现不支持，传入非空值将抛出异常</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>本次领取到的事件信息</returns>
    /// <exception cref="NotSupportedException"><paramref name="filter"/> 不为空</exception>
    public async Task<List<OutgoingEventInfo>> GetWaitingEventsAsync(
        int maxCount,
        Expression<Func<IOutgoingEventInfo, bool>>? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (filter is not null)
        {
            throw new NotSupportedException("SqlSugar 发件箱暂不支持 filter 参数，请改为在消费端筛选。");
        }

        if (maxCount <= 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        using (_currentTenant.Change(null))
        {
            var client = _clientResolver.GetCurrentClient();
            var claimed = await ClaimAsync(client, maxCount, cancellationToken);

            return [.. claimed.Select(EventOutboxMapper.ToEventInfo)];
        }
    }

    /// <summary>
    /// 删除指定的事件信息
    /// </summary>
    /// <param name="id">事件唯一标识符</param>
    public async Task DeleteAsync(Guid id)
    {
        await DeleteManyAsync([id]);
    }

    /// <summary>
    /// 批量删除事件信息
    /// </summary>
    /// <param name="ids">事件唯一标识符集合</param>
    public async Task DeleteManyAsync(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var idList = ids.Distinct().ToList();
        if (idList.Count == 0)
        {
            return;
        }

        using (_currentTenant.Change(null))
        {
            var client = _clientResolver.GetCurrentClient();
            await client.Deleteable<SysEventOutbox>().In(idList).ExecuteCommandAsync();
        }
    }

    /// <summary>
    /// 确认当前租户与平台使用同一套数据库布局
    /// </summary>
    /// <exception cref="InvalidOperationException">当前租户使用独立于平台的数据库布局</exception>
    private void EnsureSharedLayout()
    {
        if (_currentTenant.Id is not > 0)
        {
            return;
        }

        var currentLayout = _clientResolver.GetCurrentLayoutConfigIds();

        IReadOnlyList<string> platformLayout;
        using (_currentTenant.Change(null))
        {
            platformLayout = _clientResolver.GetCurrentLayoutConfigIds();
        }

        if (currentLayout.Count == 0 || platformLayout.Count == 0 ||
            !string.Equals(currentLayout[0], platformLayout[0], StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"发件箱不支持租户独立库：当前租户 {_currentTenant.Id} 使用独立于平台的数据库布局，" +
                "写入其中的事件不会被发送循环投递。请改用共享库的隔离方式，或不要在该租户上下文中发布分布式事件。");
        }
    }

    /// <summary>
    /// 以条件更新加领取令牌领取一批记录
    /// </summary>
    /// <remarks>
    /// 候选全被其他实例抢先领走时另选一批，最多三轮；返回空集合表示没有可领取的记录。
    /// </remarks>
    private async Task<List<SysEventOutbox>> ClaimAsync(ISqlSugarClient client, int maxCount, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxClaimAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = _timeProvider.GetUtcNow();
            var staleBefore = now - _options.ClaimTimeout;
            var claimToken = Guid.NewGuid().ToString("N");

            var candidateIds = await client.Queryable<SysEventOutbox>()
                .Where(item => item.Status == SysEventOutbox.StatusPending
                    || (item.Status == SysEventOutbox.StatusClaimed && item.ClaimTime != null && item.ClaimTime < staleBefore))
                .OrderBy(item => item.CreatedTime)
                .Take(maxCount)
                .Select(item => item.BasicId)
                .ToListAsync(cancellationToken);

            if (candidateIds.Count == 0)
            {
                return [];
            }

            var affected = await client.Updateable<SysEventOutbox>()
                .SetColumns(item => new SysEventOutbox
                {
                    Status = SysEventOutbox.StatusClaimed,
                    ClaimToken = claimToken,
                    ClaimTime = now
                })
                .Where(item => candidateIds.Contains(item.BasicId)
                    && (item.Status == SysEventOutbox.StatusPending
                        || (item.Status == SysEventOutbox.StatusClaimed && item.ClaimTime != null && item.ClaimTime < staleBefore)))
                .ExecuteCommandAsync(cancellationToken);

            if (affected == 0)
            {
                continue;
            }

            return await client.Queryable<SysEventOutbox>()
                .Where(item => item.ClaimToken == claimToken)
                .OrderBy(item => item.CreatedTime)
                .ToListAsync(cancellationToken);
        }

        return [];
    }
}
