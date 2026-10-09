// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.BasicApp.Saas.Infrastructure.Upgrade;

namespace XiHan.BasicApp.Saas.Tests.Upgrade;

/// <summary>
/// 数据库类型与升级脚本方言目录的对照
/// </summary>
public sealed class UpgradeScriptDialectTests
{
    [Theory]
    [InlineData(DbType.PostgreSQL, "pgsql")]
    [InlineData(DbType.SqlServer, "mssql")]
    [InlineData(DbType.MySql, "mysql")]
    [InlineData(DbType.MySqlConnector, "mysql")]
    public void 三种数据库对应各自方言目录(DbType dbType, string expected)
    {
        Assert.Equal(expected, UpgradeScriptDialect.From(dbType));
    }

    [Theory]
    [InlineData(DbType.Sqlite)]
    [InlineData(DbType.Oracle)]
    [InlineData(DbType.Dm)]
    public void 未支持的数据库没有方言目录(DbType dbType)
    {
        Assert.Null(UpgradeScriptDialect.From(dbType));
    }

    [Theory]
    [InlineData("pgsql", true)]
    [InlineData("MSSQL", true)]
    [InlineData("mysql", true)]
    [InlineData("postgres", false)]
    [InlineData("oracle", false)]
    public void 只认得三个方言目录名(string directoryName, bool expected)
    {
        Assert.Equal(expected, UpgradeScriptDialect.IsKnown(directoryName));
    }

    [Fact]
    public void 已知方言依序为pgsql_mssql_mysql()
    {
        Assert.Equal(["pgsql", "mssql", "mysql"], UpgradeScriptDialect.All);
    }
}
