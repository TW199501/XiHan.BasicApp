// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.BasicApp.Saas.Infrastructure.Upgrade;
using XiHan.Framework.Upgrade.Enums;
using XiHan.Framework.Upgrade.Services;

namespace XiHan.BasicApp.Saas.Tests.Upgrade;

/// <summary>
/// 真实升级引擎搭配方言提供者：只执行本方言脚本，缺方言时失败中止
/// </summary>
public sealed class UpgradeEngineDialectTests : IDisposable
{
    private readonly TempUpgradeScriptTree _tree = new();
    private readonly InMemoryUpgradeVersionStore _store = new();
    private readonly RecordingUpgradeLockProvider _lock = new();
    private readonly RecordingMigrationExecutor _executor = new();

    public void Dispose()
    {
        _tree.Dispose();
    }

    [Fact]
    public async Task SqlServer库_有mssql目录时只执行mssql脚本并推进版本()
    {
        _tree.Add("5.6.0/5.6.0.sql", "pg-5.6.0");
        _tree.Add("5.7.0/pgsql/5.7.0.sql", "pg-5.7.0");
        _tree.Add("5.7.0/mssql/5.7.0.sql", "mssql-5.7.0");
        _tree.Add("5.7.0/mysql/5.7.0.sql", "mysql-5.7.0");
        _store.Seed("5.6.0");

        var result = await CreateEngine(DbType.SqlServer).ExecuteAsync();

        Assert.Equal(UpgradeStatus.Completed, result.Status);
        Assert.Equal(["mssql-5.7.0"], _executor.ExecutedSql);
        Assert.Equal("5.7.0", _store.State?.DbVersion);
        var history = Assert.Single(_store.Histories);
        Assert.Equal("5.7.0", history.Version);
        Assert.Equal("5.7.0.sql", history.ScriptName);
        Assert.True(history.Success);
        Assert.True(_lock.AllReleased);
    }

    [Fact]
    public async Task MySql库_有mysql目录时只执行mysql脚本()
    {
        _tree.Add("5.7.0/pgsql/5.7.0.sql", "pg-5.7.0");
        _tree.Add("5.7.0/mssql/5.7.0.sql", "mssql-5.7.0");
        _tree.Add("5.7.0/mysql/5.7.0.sql", "mysql-5.7.0");
        _store.Seed("5.6.0");

        var result = await CreateEngine(DbType.MySql).ExecuteAsync();

        Assert.Equal(UpgradeStatus.Completed, result.Status);
        Assert.Equal(["mysql-5.7.0"], _executor.ExecutedSql);
        Assert.Equal("5.7.0", _store.State?.DbVersion);
    }

    [Fact]
    public async Task PostgreSQL库_根层脚本照常执行()
    {
        _tree.Add("5.6.0/5.6.0.sql", "pg-5.6.0");
        _tree.Add("5.7.0/5.7.0.sql", "pg-5.7.0");
        _store.Seed("5.6.0");

        var result = await CreateEngine(DbType.PostgreSQL).ExecuteAsync();

        Assert.Equal(UpgradeStatus.Completed, result.Status);
        Assert.Equal(["pg-5.7.0"], _executor.ExecutedSql);
        Assert.Equal("5.7.0", _store.State?.DbVersion);
    }

    [Fact]
    public async Task SqlServer库_新版本只有根层时失败_不执行脚本_释放锁_版本不变()
    {
        _tree.Add("5.6.0/5.6.0.sql", "pg-5.6.0");
        _tree.Add("5.7.0/5.7.0.sql", "pg-5.7.0");
        _store.Seed("5.6.0");

        var result = await CreateEngine(DbType.SqlServer).ExecuteAsync();

        Assert.Equal(UpgradeStatus.Failed, result.Status);
        Assert.Contains(Path.Combine("5.7.0", "mssql", UpgradeScriptCatalog.MissingScriptFileName), result.Message);
        Assert.Empty(_executor.ExecutedSql);
        Assert.Empty(_store.Histories);
        Assert.Equal("5.6.0", _store.State?.DbVersion);
        Assert.False(_store.State?.IsUpgrading);
        Assert.True(_lock.AllReleased);
    }

    [Fact]
    public async Task SqlServer库_缺方言版本之前的版本照常推进()
    {
        _tree.Add("5.6.0/5.6.0.sql", "pg-5.6.0");
        _tree.Add("5.6.0/mssql/5.6.0.sql", "mssql-5.6.0");
        _tree.Add("5.7.0/5.7.0.sql", "pg-5.7.0");
        _store.Seed("5.5.0");

        var result = await CreateEngine(DbType.SqlServer).ExecuteAsync();

        Assert.Equal(UpgradeStatus.Failed, result.Status);
        Assert.Equal(["mssql-5.6.0"], _executor.ExecutedSql);
        Assert.Equal("5.6.0", _store.State?.DbVersion);
        Assert.True(_lock.AllReleased);
    }

    [Fact]
    public async Task 已是最新的SqlServer库_历史版本缺方言不受影响()
    {
        _tree.Add("5.6.0/5.6.0.sql", "pg-5.6.0");
        _tree.Add("5.7.0/5.7.0.sql", "pg-5.7.0");
        _store.Seed("5.7.0");

        var result = await CreateEngine(DbType.SqlServer).ExecuteAsync();

        Assert.Equal(UpgradeStatus.Normal, result.Status);
        Assert.Empty(_executor.ExecutedSql);
    }

    [Fact]
    public async Task 新建的SqlServer库_登记全域最新版本()
    {
        _tree.Add("5.6.0/5.6.0.sql", "pg-5.6.0");
        _tree.Add("5.7.0/5.7.0.sql", "pg-5.7.0");

        var created = await CreateEngine(DbType.SqlServer).BaselineAsync();

        Assert.True(created);
        Assert.Equal("5.7.0", _store.State?.DbVersion);
        Assert.Empty(_executor.ExecutedSql);
    }

    private UpgradeEngine CreateEngine(DbType dbType)
    {
        return UpgradeTestDoubles.CreateEngine(_tree.RootPath, UpgradeTestDoubles.CreateResolver(dbType), _store, _lock, _executor);
    }
}
