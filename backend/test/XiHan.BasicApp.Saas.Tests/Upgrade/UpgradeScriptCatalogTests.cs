// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using XiHan.BasicApp.Saas.Infrastructure.Upgrade;

namespace XiHan.BasicApp.Saas.Tests.Upgrade;

/// <summary>
/// 升级脚本目录的扫描、验证与按方言取清单
/// </summary>
public sealed class UpgradeScriptCatalogTests : IDisposable
{
    private readonly TempUpgradeScriptTree _tree = new();

    public void Dispose()
    {
        _tree.Dispose();
    }

    [Fact]
    public void 只有根层脚本时_PostgreSQL取根层_其余方言得占位()
    {
        var rootScript = _tree.Add("5.6.0/5.6.0.sql");

        var catalog = UpgradeScriptCatalog.Load(_tree.RootPath);

        var postgreSql = Assert.Single(catalog.For(UpgradeScriptDialect.PostgreSql));
        Assert.Equal("5.6.0", postgreSql.Version);
        Assert.Equal("5.6.0.sql", postgreSql.ScriptName);
        Assert.Equal(rootScript, postgreSql.ScriptPath);

        foreach (var dialect in new[] { UpgradeScriptDialect.SqlServer, UpgradeScriptDialect.MySql })
        {
            var placeholder = Assert.Single(catalog.For(dialect));
            Assert.Equal("5.6.0", placeholder.Version);
            Assert.Equal($"[缺少 {dialect} 方言脚本]", placeholder.ScriptName);
            Assert.Equal(Path.Combine(_tree.RootPath, "5.6.0", dialect, UpgradeScriptCatalog.MissingScriptFileName), placeholder.ScriptPath);
            Assert.False(File.Exists(placeholder.ScriptPath));
        }
    }

    [Fact]
    public void 三个方言子目录各取各的_同版本多个文件按文件名排序()
    {
        var postgreSql = _tree.Add("5.7.0/pgsql/5.7.0.sql");
        _tree.Add("5.7.0/mssql/5.7.0-b.sql");
        _tree.Add("5.7.0/mssql/5.7.0-a.sql");
        var mySql = _tree.Add("5.7.0/mysql/5.7.0.sql");

        var catalog = UpgradeScriptCatalog.Load(_tree.RootPath);

        Assert.Equal([postgreSql], catalog.For(UpgradeScriptDialect.PostgreSql).Select(script => script.ScriptPath));
        Assert.Equal(["5.7.0-a.sql", "5.7.0-b.sql"], catalog.For(UpgradeScriptDialect.SqlServer).Select(script => script.ScriptName));
        Assert.Equal([mySql], catalog.For(UpgradeScriptDialect.MySql).Select(script => script.ScriptPath));
    }

    [Fact]
    public void 根层配mssql与mysql子目录合法_根层即PostgreSQL()
    {
        var rootScript = _tree.Add("5.7.0/5.7.0.sql");
        var sqlServer = _tree.Add("5.7.0/mssql/5.7.0.sql");
        var mySql = _tree.Add("5.7.0/mysql/5.7.0.sql");

        var catalog = UpgradeScriptCatalog.Load(_tree.RootPath);

        Assert.Equal(rootScript, Assert.Single(catalog.For(UpgradeScriptDialect.PostgreSql)).ScriptPath);
        Assert.Equal(sqlServer, Assert.Single(catalog.For(UpgradeScriptDialect.SqlServer)).ScriptPath);
        Assert.Equal(mySql, Assert.Single(catalog.For(UpgradeScriptDialect.MySql)).ScriptPath);
    }

    [Fact]
    public void 同一版本根层与pgsql子目录并存时抛异常()
    {
        _tree.Add("5.7.0/5.7.0.sql");
        _tree.Add("5.7.0/pgsql/5.7.0.sql");

        var exception = Assert.Throws<InvalidOperationException>(() => UpgradeScriptCatalog.Load(_tree.RootPath));

        Assert.Contains("5.7.0", exception.Message);
        Assert.Contains("pgsql", exception.Message);
    }

    [Fact]
    public void 版本目录下有未知子目录时抛异常()
    {
        _tree.Add("5.7.0/postgres/5.7.0.sql");

        var exception = Assert.Throws<InvalidOperationException>(() => UpgradeScriptCatalog.Load(_tree.RootPath));

        Assert.Contains("5.7.0", exception.Message);
        Assert.Contains("postgres", exception.Message);
    }

    [Fact]
    public void 同一版本有仅大小写不同的方言目录时抛异常()
    {
        _tree.Add("5.7.0/mssql/5.7.0.sql");
        Directory.CreateDirectory(_tree.PathOf("5.7.0/MSSQL"));
        Assert.SkipWhen(Directory.GetDirectories(_tree.PathOf("5.7.0")).Length < 2, "文件系统不区分大小写，无法建立仅大小写不同的目录");
        _tree.Add("5.7.0/MSSQL/5.7.0.sql");

        var exception = Assert.Throws<InvalidOperationException>(() => UpgradeScriptCatalog.Load(_tree.RootPath));

        Assert.Contains("5.7.0", exception.Message);
        Assert.Contains("mssql", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MSSQL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 方言子目录里的子目录被忽略()
    {
        var sqlServer = _tree.Add("5.7.0/mssql/5.7.0.sql");
        _tree.Add("5.7.0/mssql/archive/old.sql");

        var catalog = UpgradeScriptCatalog.Load(_tree.RootPath);

        Assert.Equal(sqlServer, Assert.Single(catalog.For(UpgradeScriptDialect.SqlServer)).ScriptPath);
    }

    [Fact]
    public void 未支持的数据库全部是占位_各方言最高版本相同()
    {
        _tree.Add("5.6.0/5.6.0.sql");
        _tree.Add("5.7.0/pgsql/5.7.0.sql");
        _tree.Add("5.7.0/mssql/5.7.0.sql");
        _tree.Add("5.7.0/mysql/5.7.0.sql");

        var catalog = UpgradeScriptCatalog.Load(_tree.RootPath);

        var sqlite = catalog.For("sqlite");
        Assert.Equal(["5.6.0", "5.7.0"], sqlite.Select(script => script.Version));
        Assert.All(sqlite, script => Assert.Equal("[缺少 sqlite 方言脚本]", script.ScriptName));
        Assert.All(sqlite, script => Assert.False(File.Exists(script.ScriptPath)));

        foreach (var dialect in UpgradeScriptDialect.All.Append("sqlite"))
        {
            Assert.Equal("5.7.0", catalog.For(dialect)[^1].Version);
        }
    }

    [Fact]
    public void 非语义版本目录与根目录平铺的脚本被忽略()
    {
        _tree.Add("draft/draft.sql");
        _tree.Add("flat.sql");
        _tree.Add("5.6.0/5.6.0.sql");

        var catalog = UpgradeScriptCatalog.Load(_tree.RootPath);

        Assert.Equal(["5.6.0"], catalog.Versions);
    }

    [Fact]
    public void 没有任何脚本的版本目录被忽略()
    {
        Directory.CreateDirectory(Path.Combine(_tree.RootPath, "5.5.0"));
        _tree.Add("5.6.0/5.6.0.sql");

        var catalog = UpgradeScriptCatalog.Load(_tree.RootPath);

        Assert.Equal(["5.6.0"], catalog.Versions);
    }

    [Fact]
    public void 版本按语义版本升序()
    {
        _tree.Add("5.10.0/5.10.0.sql");
        _tree.Add("5.9.0/5.9.0.sql");

        var catalog = UpgradeScriptCatalog.Load(_tree.RootPath);

        Assert.Equal(["5.9.0", "5.10.0"], catalog.Versions);
        Assert.Equal(["5.9.0", "5.10.0"], catalog.For(UpgradeScriptDialect.SqlServer).Select(script => script.Version));
    }

    [Fact]
    public void 根目录不存在时没有任何脚本()
    {
        var catalog = UpgradeScriptCatalog.Load(Path.Combine(_tree.RootPath, "missing"));

        Assert.Empty(catalog.Versions);
        Assert.Empty(catalog.For(UpgradeScriptDialect.PostgreSql));
    }
}
