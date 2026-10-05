// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.BasicApp.Saas.Infrastructure.Upgrade;

namespace XiHan.BasicApp.Saas.Tests.Upgrade;

/// <summary>
/// 方言提供者按当前连接的数据库类型取脚本
/// </summary>
public sealed class DialectAwareUpgradeScriptProviderTests : IDisposable
{
    private readonly TempUpgradeScriptTree _tree = new();

    public void Dispose()
    {
        _tree.Dispose();
    }

    [Theory]
    [InlineData(DbType.PostgreSQL, "5.7.0/pgsql/5.7.0.sql")]
    [InlineData(DbType.SqlServer, "5.7.0/mssql/5.7.0.sql")]
    [InlineData(DbType.MySql, "5.7.0/mysql/5.7.0.sql")]
    [InlineData(DbType.MySqlConnector, "5.7.0/mysql/5.7.0.sql")]
    public async Task 按当前连接的数据库类型只取对应方言(DbType dbType, string expected)
    {
        AddAllDialects("5.7.0");

        var scripts = await UpgradeTestDoubles.CreateProvider(_tree.RootPath, dbType).GetScriptsAsync();

        Assert.Equal(_tree.PathOf(expected), Assert.Single(scripts).ScriptPath);
    }

    [Fact]
    public async Task PostgreSQL取根层历史脚本()
    {
        var rootScript = _tree.Add("5.6.0/5.6.0.sql");

        var scripts = await UpgradeTestDoubles.CreateProvider(_tree.RootPath, DbType.PostgreSQL).GetScriptsAsync();

        Assert.Equal(rootScript, Assert.Single(scripts).ScriptPath);
    }

    [Fact]
    public async Task 未支持的数据库以类型名标出占位()
    {
        AddAllDialects("5.7.0");

        var scripts = await UpgradeTestDoubles.CreateProvider(_tree.RootPath, DbType.Sqlite).GetScriptsAsync();

        var placeholder = Assert.Single(scripts);
        Assert.Equal("[缺少 sqlite 方言脚本]", placeholder.ScriptName);
        Assert.Equal(Path.Combine(_tree.RootPath, "5.7.0", "sqlite", UpgradeScriptCatalog.MissingScriptFileName), placeholder.ScriptPath);
    }

    [Fact]
    public async Task 根目录不存在时没有脚本()
    {
        var provider = UpgradeTestDoubles.CreateProvider(Path.Combine(_tree.RootPath, "missing"), DbType.PostgreSQL);

        Assert.Empty(await provider.GetScriptsAsync());
    }

    [Fact]
    public async Task 目录结构错误时抛异常()
    {
        _tree.Add("5.7.0/5.7.0.sql");
        _tree.Add("5.7.0/pgsql/5.7.0.sql");
        var provider = UpgradeTestDoubles.CreateProvider(_tree.RootPath, DbType.SqlServer);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetScriptsAsync());
    }

    [Fact]
    public void 相对路径按应用目录解析_绝对路径原样使用()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "UpdateScripts"), DialectAwareUpgradeScriptProvider.ResolveRootPath("UpdateScripts"));
        Assert.Equal(_tree.RootPath, DialectAwareUpgradeScriptProvider.ResolveRootPath(_tree.RootPath));
    }

    private void AddAllDialects(string version)
    {
        foreach (var dialect in UpgradeScriptDialect.All)
        {
            _tree.Add($"{version}/{dialect}/{version}.sql");
        }
    }
}
