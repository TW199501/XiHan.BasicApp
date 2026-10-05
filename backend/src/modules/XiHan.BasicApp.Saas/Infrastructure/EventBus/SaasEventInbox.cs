// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.EventBus.Abstractions.Distributed;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.BasicApp.Saas.Infrastructure.EventBus;

/// <summary>
/// 收件箱的 SqlSugar 实现
/// </summary>
/// <remarks>
/// 全部读写脱离租户上下文在平台主库执行；以去重键唯一索引按消息标识去重。
/// 完结与重试只作用于已领取状态的记录，已完结的记录不会被改回待处理。
/// 消息标识以空白字符开头或结尾时拒绝入箱。
/// </remarks>
public class SaasEventInbox : IEventInbox
{
    private const int MaxClaimAttempts = 3;

    private readonly ISqlSugarClientResolver _clientResolver;
    private readonly ICurrentTenant _currentTenant;
    private readonly ILogger<SaasEventInbox> _logger;
    private readonly SaasEventBoxOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="clientResolver">客户端解析器</param>
    /// <param name="currentTenant">当前租户</param>
    /// <param name="options">收发件箱存储配置</param>
    /// <param name="logger">日志器</param>
    /// <param name="timeProvider">时钟，为 null 时使用系统时钟</param>
    public SaasEventInbox(
        ISqlSugarClientResolver clientResolver,
        ICurrentTenant currentTenant,
        IOptions<SaasEventBoxOptions> options,
        ILogger<SaasEventInbox> logger,
        TimeProvider? timeProvider = null)
    {
        _clientResolver = clientResolver;
        _currentTenant = currentTenant;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 将事件信息添加到收件箱，去重键已存在时忽略
    /// </summary>
    /// <param name="incomingEvent">入站事件信息</param>
    /// <exception cref="ArgumentException">消息标识以空白字符开头或结尾</exception>
    public async Task EnqueueAsync(IncomingEventInfo incomingEvent)
    {
        ArgumentNullException.ThrowIfNull(incomingEvent);

        if (HasSurroundingWhiteSpace(incomingEvent.MessageId))
        {
            throw new ArgumentException("消息标识不能以空白字符开头或结尾。", nameof(incomingEvent));
        }

        var entity = EventInboxMapper.ToEntity(incomingEvent);
        var dedupKey = entity.DedupKey;

        using (_currentTenant.Change(null))
        {
            var client = _clientResolver.GetCurrentClient();

            try
            {
                await client.Insertable(entity).ExecuteCommandAsync();
            }
            catch (Exception ex)
            {
                if (!client.Ado.IsNoTran())
                {
                    throw;
                }

                if (!await IsDuplicatedAsync(client, dedupKey))
                {
                    throw;
                }

                _logger.LogDebug(ex, "收件箱已存在去重键为 {DedupKey} 的记录，本次入箱已忽略。", dedupKey);
            }
        }
    }

    /// <summary>
    /// 判断消息标识是否以空白字符开头或结尾；空值与只含空白的值返回 false
    /// </summary>
    private static bool HasSurroundingWhiteSpace(string? messageId)
    {
        return !string.IsNullOrWhiteSpace(messageId) &&
               (char.IsWhiteSpace(messageId[0]) || char.IsWhiteSpace(messageId[^1]));
    }

    /// <summary>
    /// 查询去重键是否已存在，查询失败时记录警告并返回 false
    /// </summary>
    private async Task<bool> IsDuplicatedAsync(ISqlSugarClient client, string dedupKey)
    {
        try
        {
            return await client.Queryable<SysEventInbox>().AnyAsync(item => item.DedupKey == dedupKey);
        }
        catch (Exception queryException)
        {
            _logger.LogWarning(queryException, "收件箱去重查询失败，去重键 {DedupKey}，改为抛出原始插入异常。", dedupKey);
            return false;
        }
    }

    /// <summary>
    /// 判断指定消息标识是否已入箱
    /// </summary>
    /// <param name="messageId">消息标识</param>
    /// <returns>已入箱时为 true</returns>
    public async Task<bool> ExistsByMessageIdAsync(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        using (_currentTenant.Change(null))
        {
            var client = _clientResolver.GetCurrentClient();
            return await client.Queryable<SysEventInbox>().AnyAsync(item => item.DedupKey == messageId);
        }
    }

    /// <summary>
    /// 领取一批待处理的事件信息
    /// </summary>
    /// <param name="maxCount">最大数量</param>
    /// <param name="filter">过滤条件，本实现不支持，传入非空值将抛出异常</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>本次领取到的事件信息</returns>
    /// <exception cref="NotSupportedException"><paramref name="filter"/> 不为空</exception>
    public async Task<List<IncomingEventInfo>> GetWaitingEventsAsync(
        int maxCount,
        Expression<Func<IIncomingEventInfo, bool>>? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (filter is not null)
        {
            throw new NotSupportedException("SqlSugar 收件箱暂不支持 filter 参数，请改为在事件处理器内筛选。");
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

            return [.. claimed.Select(EventInboxMapper.ToEventInfo)];
        }
    }

    /// <summary>
    /// 标记为已处理
    /// </summary>
    /// <param name="id">事件标识</param>
    public async Task MarkAsProcessedAsync(Guid id)
    {
        await MarkAsHandledAsync(id, SysEventInbox.StatusProcessed);
    }

    /// <summary>
    /// 放回待处理并设定下次重试时刻
    /// </summary>
    /// <param name="id">事件标识</param>
    /// <param name="retryCount">重试次数</param>
    /// <param name="nextRetryTime">下次重试时刻，为 null 时立即可重试</param>
    public async Task RetryLaterAsync(Guid id, int retryCount, DateTime? nextRetryTime)
    {
        var nextRetry = nextRetryTime.HasValue ? EventInboxMapper.ToOffset(nextRetryTime.Value) : _timeProvider.GetUtcNow();

        using (_currentTenant.Change(null))
        {
            var client = _clientResolver.GetCurrentClient();

            await client.Updateable<SysEventInbox>()
                .SetColumns(item => new SysEventInbox
                {
                    Status = SysEventInbox.StatusPending,
                    RetryCount = retryCount,
                    NextRetryTime = nextRetry,
                    ClaimToken = null,
                    ClaimTime = null
                })
                .Where(item => item.BasicId == id && item.Status == SysEventInbox.StatusClaimed)
                .ExecuteCommandAsync();
        }
    }

    /// <summary>
    /// 标记为已丢弃
    /// </summary>
    /// <param name="id">事件标识</param>
    public async Task MarkAsDiscardAsync(Guid id)
    {
        await MarkAsHandledAsync(id, SysEventInbox.StatusDiscarded);
    }

    /// <summary>
    /// 删除超过保留期的已处理与已丢弃记录
    /// </summary>
    public async Task DeleteOldEventsAsync()
    {
        var cutoff = _timeProvider.GetUtcNow() - _options.InboxRetentionPeriod;

        using (_currentTenant.Change(null))
        {
            var client = _clientResolver.GetCurrentClient();

            await client.Deleteable<SysEventInbox>()
                .Where(item => (item.Status == SysEventInbox.StatusProcessed || item.Status == SysEventInbox.StatusDiscarded)
                    && item.HandledTime != null
                    && item.HandledTime <= cutoff)
                .ExecuteCommandAsync();
        }
    }

    /// <summary>
    /// 把已领取的记录标记为完结状态
    /// </summary>
    private async Task MarkAsHandledAsync(Guid id, int status)
    {
        var now = _timeProvider.GetUtcNow();

        using (_currentTenant.Change(null))
        {
            var client = _clientResolver.GetCurrentClient();

            await client.Updateable<SysEventInbox>()
                .SetColumns(item => new SysEventInbox
                {
                    Status = status,
                    NextRetryTime = null,
                    ClaimToken = null,
                    ClaimTime = null,
                    HandledTime = now
                })
                .Where(item => item.BasicId == id && item.Status == SysEventInbox.StatusClaimed)
                .ExecuteCommandAsync();
        }
    }

    /// <summary>
    /// 以条件更新加领取令牌领取一批记录
    /// </summary>
    /// <remarks>
    /// 候选全被其他实例抢先领走时另选一批，最多三轮；返回空集合表示没有可领取的记录。
    /// </remarks>
    private async Task<List<SysEventInbox>> ClaimAsync(ISqlSugarClient client, int maxCount, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxClaimAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = _timeProvider.GetUtcNow();
            var staleBefore = now - _options.ClaimTimeout;
            var claimToken = Guid.NewGuid().ToString("N");

            var candidateIds = await client.Queryable<SysEventInbox>()
                .Where(item => (item.Status == SysEventInbox.StatusPending && (item.NextRetryTime == null || item.NextRetryTime <= now))
                    || (item.Status == SysEventInbox.StatusClaimed && item.ClaimTime != null && item.ClaimTime < staleBefore))
                .OrderBy(item => item.CreatedTime)
                .Take(maxCount)
                .Select(item => item.BasicId)
                .ToListAsync(cancellationToken);

            if (candidateIds.Count == 0)
            {
                return [];
            }

            var affected = await client.Updateable<SysEventInbox>()
                .SetColumns(item => new SysEventInbox
                {
                    Status = SysEventInbox.StatusClaimed,
                    ClaimToken = claimToken,
                    ClaimTime = now
                })
                .Where(item => candidateIds.Contains(item.BasicId)
                    && ((item.Status == SysEventInbox.StatusPending && (item.NextRetryTime == null || item.NextRetryTime <= now))
                        || (item.Status == SysEventInbox.StatusClaimed && item.ClaimTime != null && item.ClaimTime < staleBefore)))
                .ExecuteCommandAsync(cancellationToken);

            if (affected == 0)
            {
                continue;
            }

            return await client.Queryable<SysEventInbox>()
                .Where(item => candidateIds.Contains(item.BasicId) && item.ClaimToken == claimToken)
                .OrderBy(item => item.CreatedTime)
                .ToListAsync(cancellationToken);
        }

        return [];
    }
}
