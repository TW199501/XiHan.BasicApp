// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SqlSugar;
using XiHan.BasicApp.Saas.Extensions;
using XiHan.Framework.Data.SqlSugar.Options;

namespace XiHan.BasicApp.Saas.Tests;

/// <summary>
/// SaaS 的 SQL Server 连接约定测试：连接配置钩子
/// </summary>
public sealed class SaasSqlServerConnectionConventionTests
{
    /// <summary>
    /// 注册的连接钩子在已有钩子之后为 SQL Server 开启 nvarchar，保留已有钩子写入的 MoreSettings，其他数据库不变
    /// </summary>
    [Fact]
    public void ConnectionConvention_RunsAfterExistingHookAndEnablesNvarcharForSqlServer()
    {
        var services = new ServiceCollection();
        services.Configure<XiHanSqlSugarCoreOptions>(options => options.ConfigureConnectionConfigs = configs => configs[0].MoreSettings = new ConnMoreSettings { IsAutoUpdateQueryFilter = true });
        services.AddSaasSqlServerConnectionConvention();
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<XiHanSqlSugarCoreOptions>>().Value;
        var sqlServer = new ConnectionConfig { DbType = DbType.SqlServer, ConnectionString = "Server=h;Database=d" };
        var postgres = new ConnectionConfig { DbType = DbType.PostgreSQL, ConnectionString = "Host=h;Database=d", MoreSettings = null };

        options.ConfigureConnectionConfigs!([sqlServer, postgres]);

        Assert.True(sqlServer.MoreSettings.SqlServerCodeFirstNvarchar);
        Assert.True(sqlServer.MoreSettings.IsAutoUpdateQueryFilter);
        Assert.Null(postgres.MoreSettings);
    }

    /// <summary>
    /// 连接约定注册两次，已有钩子只执行一次
    /// </summary>
    [Fact]
    public void ConnectionConvention_RegisteredTwice_RunsExistingHookOnce()
    {
        var hookCalls = 0;
        var services = new ServiceCollection();
        services.Configure<XiHanSqlSugarCoreOptions>(options => options.ConfigureConnectionConfigs = _ => hookCalls++);
        services.AddSaasSqlServerConnectionConvention();
        services.AddSaasSqlServerConnectionConvention();
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<XiHanSqlSugarCoreOptions>>().Value;
        var sqlServer = new ConnectionConfig { DbType = DbType.SqlServer, ConnectionString = "Server=h;Database=d" };

        options.ConfigureConnectionConfigs!([sqlServer]);

        Assert.Equal(1, hookCalls);
        Assert.True(sqlServer.MoreSettings.SqlServerCodeFirstNvarchar);
    }

    /// <summary>
    /// 与 MySQL 连接约定同时注册时各管各的数据库
    /// </summary>
    [Fact]
    public void ConnectionConvention_WithMySqlConvention_AppliesBoth()
    {
        var services = new ServiceCollection();
        services.AddSaasMySqlConnectionConvention();
        services.AddSaasSqlServerConnectionConvention();
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<XiHanSqlSugarCoreOptions>>().Value;
        var mysql = new ConnectionConfig { DbType = DbType.MySql, ConnectionString = "Server=h;Database=d", MoreSettings = null };
        var sqlServer = new ConnectionConfig { DbType = DbType.SqlServer, ConnectionString = "Server=h;Database=d" };

        options.ConfigureConnectionConfigs!([mysql, sqlServer]);

        Assert.Equal(MySqlDateTimeKind.Utc, new MySqlConnectionStringBuilder(mysql.ConnectionString).DateTimeKind);
        Assert.Null(mysql.MoreSettings);
        Assert.True(sqlServer.MoreSettings.SqlServerCodeFirstNvarchar);
        Assert.Equal("Server=h;Database=d", sqlServer.ConnectionString);
    }
}
