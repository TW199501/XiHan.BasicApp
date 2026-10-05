// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.Upgrade;
using XiHan.BasicApp.Saas.Tests.TestDatabases;
using XiHan.Framework.Upgrade.Enums;
using XiHan.Framework.Upgrade.Services;

namespace XiHan.BasicApp.Saas.Tests.Upgrade;

/// <summary>
/// 三种数据库上的分方言升级：旧版本库只执行本方言脚本，重跑空转，缺方言时失败
/// </summary>
/// <remarks>
/// 各数据库以环境变量门控，未设置即跳过。版本表、台账表与标记表都映射到带随机后缀的表名，测试结束时删除。
/// </remarks>
public sealed class DialectUpgradeDatabaseTests : IDisposable
{
    private readonly TempUpgradeScriptTree _tree = new();
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];

    public static TheoryData<DbType> DbTypes => new() { DbType.PostgreSQL, DbType.SqlServer, DbType.MySql };

    private string VersionTable => $"e87_sys_version_{_suffix}";

    private string HistoryTable => $"e87_sys_migration_history_{_suffix}";

    private string MarkerTable => $"e87_marker_{_suffix}";

    public void Dispose()
    {
        _tree.Dispose();
    }

    [Theory]
    [MemberData(nameof(DbTypes))]
    public async Task 旧版本库只执行本方言脚本_重跑空转_缺方言时失败(DbType dbType)
    {
        var variable = VariableOf(dbType);
        var connectionString = IntegrationDatabase.GetConnectionString(variable);
        if (connectionString is null)
        {
            Assert.Skip($"未设置 {variable}，跳过。");
        }

        var config = IntegrationDatabase.CreateConnectionConfig(dbType, connectionString);
        config.ConfigId = $"E87_{dbType}_{_suffix}";
        using var db = new SqlSugarClient(config);
        EnablePrimaryKeyGeneration(db);
        db.MappingTables.Add(nameof(SysVersion), VersionTable);
        db.MappingTables.Add(nameof(SysMigrationHistory), HistoryTable);
        try
        {
            db.CodeFirst.InitTables(typeof(SysVersion), typeof(SysMigrationHistory));
            Assert.True(db.DbMaintenance.IsAnyTable(VersionTable, false), $"版本表没有建成映射后的表名 {VersionTable}");
            Assert.True(db.DbMaintenance.IsAnyTable(HistoryTable, false), $"台账表没有建成映射后的表名 {HistoryTable}");

            SeedVersion(db, "1.0.0");
            foreach (var dialect in UpgradeScriptDialect.All)
            {
                _tree.Add($"1.1.0/{dialect}/1.1.0.sql", CreateMarkerScript(dialect));
            }

            var upgraded = await CreateEngine(db).ExecuteAsync();

            Assert.Equal(UpgradeStatus.Completed, upgraded.Status);
            Assert.Equal(UpgradeScriptDialect.From(dbType), Assert.Single(db.Ado.SqlQuery<string>($"SELECT dialect FROM {MarkerTable}")));
            var history = Assert.Single(db.Queryable<SysMigrationHistory>().ToList());
            Assert.Equal("1.1.0", history.Version);
            Assert.Equal("1.1.0.sql", history.ScriptName);
            Assert.True(history.Success);
            AssertVersion(db, "1.1.0");

            var rerun = await CreateEngine(db).ExecuteAsync();

            Assert.Equal(UpgradeStatus.Normal, rerun.Status);
            _ = Assert.Single(db.Queryable<SysMigrationHistory>().ToList());
            AssertVersion(db, "1.1.0");

            _tree.Add("1.2.0/1.2.0.sql", CreateRootPostgreSqlScript());

            var rootOnly = await CreateEngine(db).ExecuteAsync();

            if (dbType == DbType.PostgreSQL)
            {
                Assert.Equal(UpgradeStatus.Completed, rootOnly.Status);
                AssertVersion(db, "1.2.0");
            }
            else
            {
                Assert.Equal(UpgradeStatus.Failed, rootOnly.Status);
                Assert.Contains(UpgradeScriptCatalog.MissingScriptFileName, rootOnly.Message);
                AssertVersion(db, "1.1.0");
            }
        }
        finally
        {
            foreach (var table in new[] { MarkerTable, HistoryTable, VersionTable })
            {
                if (db.DbMaintenance.IsAnyTable(table, false))
                {
                    _ = db.DbMaintenance.DropTable(table);
                }
            }
        }
    }

    private static string VariableOf(DbType dbType)
    {
        return dbType switch
        {
            DbType.PostgreSQL => IntegrationDatabase.PostgresVariable,
            DbType.SqlServer => IntegrationDatabase.SqlServerVariable,
            DbType.MySql => IntegrationDatabase.MySqlVariable,
            _ => throw new ArgumentOutOfRangeException(nameof(dbType), dbType, "没有对应的测试库环境变量")
        };
    }

    private UpgradeEngine CreateEngine(ISqlSugarClient db)
    {
        var resolver = UpgradeTestDoubles.CreateResolver(db);
        return UpgradeTestDoubles.CreateEngine(
            _tree.RootPath,
            resolver,
            new SaasUpgradeVersionStore(resolver, new TestCurrentTenant()),
            new SaasUpgradeLockProvider(resolver, NullLogger<SaasUpgradeLockProvider>.Instance),
            new SaasUpgradeMigrationExecutor(resolver));
    }

    private static void EnablePrimaryKeyGeneration(ISqlSugarClient db)
    {
        db.Aop.DataExecuting = (oldValue, entityInfo) =>
        {
            if (entityInfo.OperationType == DataFilterType.InsertByObject
                && entityInfo.EntityColumnInfo.IsPrimarykey
                && oldValue is long id
                && id == 0)
            {
                entityInfo.SetValue(SnowFlakeSingle.Instance.NextId());
            }
        };
    }

    private static void SeedVersion(ISqlSugarClient db, string dbVersion)
    {
        _ = db.Insertable(new SysVersion
        {
            AppVersion = UpgradeTestDoubles.AppVersion,
            DbVersion = dbVersion,
            IsUpgrading = false
        }).ExecuteCommand();
    }

    private static void AssertVersion(ISqlSugarClient db, string expected)
    {
        var row = Assert.Single(db.Queryable<SysVersion>().ToList());
        Assert.Equal(expected, row.DbVersion);
        Assert.False(row.IsUpgrading);
    }

    private string CreateMarkerScript(string dialect)
    {
        var table = MarkerTable;
        return dialect switch
        {
            UpgradeScriptDialect.PostgreSql => $"""
                CREATE TABLE IF NOT EXISTS {table} (dialect varchar(16) NOT NULL);
                INSERT INTO {table} (dialect) SELECT 'pgsql' WHERE NOT EXISTS (SELECT 1 FROM {table});
                """,
            UpgradeScriptDialect.SqlServer => $"""
                IF OBJECT_ID(N'{table}', N'U') IS NULL CREATE TABLE {table} (dialect nvarchar(16) NOT NULL);
                IF NOT EXISTS (SELECT 1 FROM {table}) INSERT INTO {table} (dialect) VALUES (N'mssql');
                """,
            UpgradeScriptDialect.MySql => $"""
                CREATE TABLE IF NOT EXISTS {table} (dialect varchar(16) NOT NULL) ENGINE=InnoDB;
                INSERT INTO {table} (dialect) SELECT 'mysql' FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM {table});
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "未知方言")
        };
    }

    private string CreateRootPostgreSqlScript()
    {
        var table = MarkerTable;
        return $"""
            INSERT INTO {table} (dialect) SELECT 'pgsql-1.2.0' WHERE NOT EXISTS (SELECT 1 FROM {table} WHERE dialect = 'pgsql-1.2.0');
            """;
    }
}
