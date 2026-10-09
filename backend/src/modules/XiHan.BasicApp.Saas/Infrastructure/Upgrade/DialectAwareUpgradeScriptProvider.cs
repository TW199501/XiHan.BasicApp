// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Options;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Upgrade.Abstractions;
using XiHan.Framework.Upgrade.Models;
using XiHan.Framework.Upgrade.Options;

namespace XiHan.BasicApp.Saas.Infrastructure.Upgrade;

/// <summary>
/// 按当前连接的数据库类型提供对应方言的升级脚本
/// </summary>
/// <remarks>
/// 当前连接由当前租户上下文经 <see cref="ISqlSugarClientResolver"/> 解析，与版本存储、迁移执行器使用同一个库。
/// 脚本布局与缺方言时的占位规则见 <see cref="UpgradeScriptCatalog"/>。
/// </remarks>
public sealed class DialectAwareUpgradeScriptProvider(
    IOptions<XiHanUpgradeOptions> options,
    ISqlSugarClientResolver clientResolver)
    : IUpgradeScriptProvider
{
    /// <summary>
    /// 获取当前连接方言的升级脚本
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>按版本、文件名升序的脚本；缺方言的版本为占位脚本</returns>
    /// <exception cref="InvalidOperationException">升级脚本目录结构不合法</exception>
    public Task<IReadOnlyList<UpgradeScript>> GetScriptsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dbType = clientResolver.GetCurrentClient().CurrentConnectionConfig.DbType;
        var dialect = UpgradeScriptDialect.From(dbType) ?? dbType.ToString().ToLowerInvariant();
        var catalog = UpgradeScriptCatalog.Load(ResolveRootPath(options.Value.MigrationsRootPath));

        return Task.FromResult(catalog.For(dialect));
    }

    /// <summary>
    /// 解析升级脚本根目录：绝对路径原样使用，相对路径基于应用目录
    /// </summary>
    /// <param name="migrationsRootPath">配置的升级脚本根目录</param>
    /// <returns>升级脚本根目录的完整路径</returns>
    public static string ResolveRootPath(string migrationsRootPath)
    {
        if (Path.IsPathRooted(migrationsRootPath))
        {
            return migrationsRootPath;
        }

        return Path.Combine(AppContext.BaseDirectory, migrationsRootPath);
    }
}
