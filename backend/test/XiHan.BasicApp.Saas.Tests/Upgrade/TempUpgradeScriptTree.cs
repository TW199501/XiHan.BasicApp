// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XiHan.BasicApp.Saas.Tests.Upgrade;

/// <summary>
/// 暂存的升级脚本目录树，释放时整棵删除
/// </summary>
internal sealed class TempUpgradeScriptTree : IDisposable
{
    /// <summary>
    /// 建立空的根目录
    /// </summary>
    public TempUpgradeScriptTree()
    {
        RootPath = Path.Combine(Path.GetTempPath(), $"xihan-upgrade-scripts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);
    }

    /// <summary>
    /// 根目录
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// 写入一个文件，路径以 / 分隔、相对于根目录
    /// </summary>
    /// <param name="relativePath">相对路径，如 5.7.0/mssql/5.7.0.sql</param>
    /// <param name="content">文件内容；不传时写入以相对路径为内容的注释</param>
    /// <returns>文件完整路径</returns>
    public string Add(string relativePath, string? content = null)
    {
        var path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? RootPath);
        File.WriteAllText(path, content ?? $"-- {relativePath}");
        return path;
    }

    /// <summary>
    /// 取相对路径对应的完整路径
    /// </summary>
    /// <param name="relativePath">相对路径，以 / 分隔</param>
    /// <returns>完整路径</returns>
    public string PathOf(string relativePath)
    {
        return Path.Combine([RootPath, .. relativePath.Split('/')]);
    }

    /// <summary>
    /// 删除整棵目录树
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
