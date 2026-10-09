// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using XiHan.BasicApp.Saas.Infrastructure.Upgrade;
using XiHan.Framework.Upgrade.Utils;

namespace XiHan.BasicApp.Saas.Tests;

/// <summary>
/// 升级脚本目录布局测试。
/// </summary>
/// <remarks>
/// 用真实的 <c>UpdateScripts</c> 检查：布局能被 <see cref="UpgradeScriptCatalog"/> 完整扫到、
/// PostgreSQL 每个版本都有脚本、自 <see cref="MultiDialectSinceVersion"/> 起每个版本三种方言齐全。
/// </remarks>
public sealed class UpgradeScriptLayoutTests
{
    /// <summary>
    /// 自此版本起，每个版本都必须同时提供 pgsql、mssql、mysql 三份脚本
    /// </summary>
    private const string MultiDialectSinceVersion = "5.6.1";

    /// <summary>
    /// 目录下全部 .sql 都能被方言目录扫到，且目录结构合法。
    /// </summary>
    [Fact]
    public void UpdateScripts_ShouldBeFullyDiscoverableByCatalog()
    {
        var rootPath = ResolveUpdateScriptsRoot();
        Assert.True(Directory.Exists(rootPath), $"升级脚本目录不存在：{rootPath}");

        var actualSqlCount = Directory.GetFiles(rootPath, "*.sql", SearchOption.AllDirectories).Length;
        Assert.True(actualSqlCount > 0, "升级脚本目录下一个 .sql 都没有，测试失去意义");

        var catalog = UpgradeScriptCatalog.Load(rootPath);
        var discoveredCount = UpgradeScriptDialect.All
            .SelectMany(dialect => catalog.For(dialect))
            .Count(script => File.Exists(script.ScriptPath));

        Assert.Equal(actualSqlCount, discoveredCount);
    }

    /// <summary>
    /// PostgreSQL 每个版本都有实际脚本。
    /// </summary>
    [Fact]
    public void UpdateScripts_PostgreSqlShouldCoverEveryVersion()
    {
        var catalog = UpgradeScriptCatalog.Load(ResolveUpdateScriptsRoot());

        var missing = catalog.For(UpgradeScriptDialect.PostgreSql)
            .Where(script => !File.Exists(script.ScriptPath))
            .Select(script => script.Version)
            .ToList();

        Assert.True(missing.Count == 0, $"这些版本缺少 PostgreSQL 脚本：{string.Join(", ", missing)}");
    }

    /// <summary>
    /// 自 <see cref="MultiDialectSinceVersion"/> 起每个版本三种方言齐全。
    /// </summary>
    [Fact]
    public void UpdateScripts_EveryDialectShouldCoverVersionsSinceMultiDialect()
    {
        var catalog = UpgradeScriptCatalog.Load(ResolveUpdateScriptsRoot());

        var missing = UpgradeScriptDialect.All
            .SelectMany(dialect => catalog.For(dialect)
                .Where(script => SemanticVersion.Compare(script.Version, MultiDialectSinceVersion) >= 0)
                .Where(script => !File.Exists(script.ScriptPath))
                .Select(script => $"{script.Version}/{dialect}"))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"自 {MultiDialectSinceVersion} 起每个版本都要有 pgsql、mssql、mysql 三份脚本，缺少：{string.Join(", ", missing)}");
    }

    /// <summary>
    /// 脚本必须落在以版本号命名的子目录里，而不是平铺在根目录下。
    /// </summary>
    [Fact]
    public void UpdateScripts_ShouldNotContainFlatSqlFiles()
    {
        var rootPath = ResolveUpdateScriptsRoot();

        var flatFiles = Directory.GetFiles(rootPath, "*.sql", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            flatFiles.Count == 0,
            $"这些脚本平铺在 UpdateScripts 根目录下，provider 只扫子目录、收不到它们：{string.Join(", ", flatFiles)}");
    }

    /// <summary>
    /// 定位仓库中的升级脚本目录
    /// </summary>
    /// <remarks>
    /// 以本测试源文件位置为锚点向上回溯，不依赖运行目录：
    /// <c>backend/test/XiHan.BasicApp.Saas.Tests/</c> → <c>backend/src/main/XiHan.BasicApp.WebHost/UpdateScripts</c>。
    /// </remarks>
    private static string ResolveUpdateScriptsRoot([CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("无法解析测试源文件目录。");

        return Path.GetFullPath(Path.Combine(
            testDirectory, "..", "..", "src", "main", "XiHan.BasicApp.WebHost", "UpdateScripts"));
    }
}
