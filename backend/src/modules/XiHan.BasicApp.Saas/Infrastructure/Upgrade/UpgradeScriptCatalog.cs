// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.Framework.Upgrade.Models;
using XiHan.Framework.Upgrade.Utils;

namespace XiHan.BasicApp.Saas.Infrastructure.Upgrade;

/// <summary>
/// 升级脚本目录：按版本收集根层与各方言子目录的脚本，并按方言给出执行清单
/// </summary>
/// <remarks>
/// 布局为 <c>&lt;根&gt;/&lt;版本&gt;/*.sql</c>（PostgreSQL）或 <c>&lt;根&gt;/&lt;版本&gt;/&lt;方言&gt;/*.sql</c>。
/// 某版本缺少指定方言的脚本时，清单中放一条指向 <c>&lt;根&gt;/&lt;版本&gt;/&lt;方言&gt;/__missing__.sql</c>（不存在）的占位脚本。
/// </remarks>
public sealed class UpgradeScriptCatalog
{
    /// <summary>
    /// 占位脚本的文件名（该文件不存在）
    /// </summary>
    public const string MissingScriptFileName = "__missing__.sql";

    private readonly IReadOnlyList<VersionScripts> _versions;

    private UpgradeScriptCatalog(IReadOnlyList<VersionScripts> versions)
    {
        _versions = versions;
    }

    /// <summary>
    /// 含脚本的版本号，按语义版本升序
    /// </summary>
    public IReadOnlyList<string> Versions => [.. _versions.Select(entry => entry.Version)];

    /// <summary>
    /// 扫描升级脚本根目录
    /// </summary>
    /// <param name="rootPath">升级脚本根目录</param>
    /// <returns>升级脚本目录；根目录不存在时不含任何版本</returns>
    /// <exception cref="InvalidOperationException">版本目录下有未知子目录、仅大小写不同的方言子目录，或根层 .sql 与 pgsql 子目录并存</exception>
    public static UpgradeScriptCatalog Load(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        if (!Directory.Exists(rootPath))
        {
            return new UpgradeScriptCatalog([]);
        }

        var versions = new List<VersionScripts>();
        foreach (var versionDirectory in Directory.GetDirectories(rootPath))
        {
            var version = Path.GetFileName(versionDirectory);
            if (!SemanticVersion.TryParse(version, out _))
            {
                continue;
            }

            var scripts = LoadVersion(version, versionDirectory);
            if (scripts.Count > 0)
            {
                versions.Add(new VersionScripts(version, versionDirectory, scripts));
            }
        }

        versions.Sort((left, right) => SemanticVersion.Compare(left.Version, right.Version));
        return new UpgradeScriptCatalog(versions);
    }

    /// <summary>
    /// 取指定方言的执行清单
    /// </summary>
    /// <param name="dialect">方言目录名，见 <see cref="UpgradeScriptDialect"/>；未支持的数据库传其类型名</param>
    /// <returns>每个版本至少一条：有该方言脚本时为实际脚本（按文件名升序），否则为一条占位脚本</returns>
    public IReadOnlyList<UpgradeScript> For(string dialect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dialect);

        var normalized = dialect.ToLowerInvariant();
        var scripts = new List<UpgradeScript>();
        foreach (var entry in _versions)
        {
            if (entry.Scripts.TryGetValue(normalized, out var files))
            {
                scripts.AddRange(files.Select(file => new UpgradeScript(entry.Version, Path.GetFileName(file), file)));
                continue;
            }

            scripts.Add(new UpgradeScript(
                entry.Version,
                $"[缺少 {normalized} 方言脚本]",
                Path.Combine(entry.DirectoryPath, normalized, MissingScriptFileName)));
        }

        return scripts;
    }

    /// <summary>
    /// 收集单个版本目录的脚本，键为方言目录名
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> LoadVersion(string version, string versionDirectory)
    {
        var scripts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var dialectDirectoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dialectDirectory in Directory.GetDirectories(versionDirectory))
        {
            var dialect = Path.GetFileName(dialectDirectory);
            if (!UpgradeScriptDialect.IsKnown(dialect))
            {
                throw new InvalidOperationException(
                    $"升级脚本版本 {version} 下的目录 {dialectDirectory} 不是已知方言（{string.Join("、", UpgradeScriptDialect.All)}）。");
            }

            if (!dialectDirectoryNames.TryAdd(dialect, dialect))
            {
                throw new InvalidOperationException(
                    $"升级脚本版本 {version} 下的目录 {dialectDirectoryNames[dialect]} 与 {dialect} 仅大小写不同，无法判定以哪份为准：{versionDirectory}");
            }

            var files = ListSqlFiles(dialectDirectory);
            if (files.Count > 0)
            {
                scripts[dialect.ToLowerInvariant()] = files;
            }
        }

        var rootFiles = ListSqlFiles(versionDirectory);
        if (rootFiles.Count > 0)
        {
            if (scripts.ContainsKey(UpgradeScriptDialect.PostgreSql))
            {
                throw new InvalidOperationException(
                    $"升级脚本版本 {version} 同时有根层 .sql 与 {UpgradeScriptDialect.PostgreSql} 目录，无法判定以哪份为准：{versionDirectory}");
            }

            scripts[UpgradeScriptDialect.PostgreSql] = rootFiles;
        }

        return scripts;
    }

    /// <summary>
    /// 列出目录本层的 .sql，按文件名升序
    /// </summary>
    private static IReadOnlyList<string> ListSqlFiles(string directory)
    {
        return [.. Directory.GetFiles(directory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// 单个版本的脚本
    /// </summary>
    /// <param name="Version">版本号</param>
    /// <param name="DirectoryPath">版本目录</param>
    /// <param name="Scripts">方言目录名到脚本完整路径的映射</param>
    private sealed record VersionScripts(string Version, string DirectoryPath, IReadOnlyDictionary<string, IReadOnlyList<string>> Scripts);
}
