// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using MySqlConnector;
using SqlSugar;
using XiHan.BasicApp.Saas.Domain.DomainServices;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Extensions;
using XiHan.BasicApp.Saas.Infrastructure.MultiTenancy;
using XiHan.BasicApp.Saas.Tests.Idempotency;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Data.SqlSugar.Options;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.BasicApp.Saas.Tests;

/// <summary>
/// SaaS 的 MySQL 连接约定测试：租户描述符与连接配置钩子
/// </summary>
public sealed class SaasMySqlConnectionConventionTests
{
    /// <summary>
    /// 库隔离的 MySQL 租户，描述符连接串带 DateTimeKind=Utc；其他数据库类型保持原样
    /// </summary>
    [Fact]
    public void TenantConnectionProvider_MySqlDescriptor_HasUtcDateTimeKind()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xihan-tenant-conn-{Guid.NewGuid():N}.db");
        try
        {
            using var client = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"DataSource={path};Pooling=False",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true
            });
            client.CodeFirst.InitTables<SysTenant>();
            InsertTenant(client, 101, TenantDatabaseType.MySql, "Server=h;Database=t101;Uid=u;Pwd=p");
            InsertTenant(client, 102, TenantDatabaseType.PostgreSql, "Host=h;Database=t102");
            var provider = CreateProvider(client);

            var mysql = provider.Resolve(101, null);
            var postgres = provider.Resolve(102, null);

            Assert.NotNull(mysql);
            Assert.Equal(DbType.MySql, mysql.DbType);
            Assert.Equal(MySqlDateTimeKind.Utc, new MySqlConnectionStringBuilder(mysql.ConnectionString).DateTimeKind);
            Assert.NotNull(postgres);
            Assert.Equal("Host=h;Database=t102", postgres.ConnectionString);
        }
        finally
        {
            SaasTestHelper.DeleteTemporaryDatabase(path);
        }
    }

    /// <summary>
    /// 注册的连接钩子在已有钩子之后规范化 MySQL 连接，并保留已有钩子的改动
    /// </summary>
    [Fact]
    public void ConnectionConvention_RunsAfterExistingHookAndNormalizesMySql()
    {
        var services = new ServiceCollection();
        services.Configure<XiHanSqlSugarCoreOptions>(options => options.ConfigureConnectionConfigs = configs => configs[0].ConnectionString += ";Connection Timeout=7");
        services.AddSaasMySqlConnectionConvention();
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<XiHanSqlSugarCoreOptions>>().Value;
        var mysql = new ConnectionConfig { DbType = DbType.MySql, ConnectionString = "Server=h;Database=d" };
        var postgres = new ConnectionConfig { DbType = DbType.PostgreSQL, ConnectionString = "Host=h;Database=d" };

        options.ConfigureConnectionConfigs!([mysql, postgres]);

        var builder = new MySqlConnectionStringBuilder(mysql.ConnectionString);
        Assert.Equal(MySqlDateTimeKind.Utc, builder.DateTimeKind);
        Assert.Equal(7u, builder.ConnectionTimeout);
        Assert.Equal("Host=h;Database=d", postgres.ConnectionString);
    }

    /// <summary>
    /// 连接约定注册两次，连接串只补一个 DateTimeKind，已有钩子只执行一次，执行前钩子只挂一层
    /// </summary>
    [Fact]
    public void ConnectionConvention_RegisteredTwice_AppliesOnce()
    {
        var hookCalls = 0;
        var services = new ServiceCollection();
        services.Configure<XiHanSqlSugarCoreOptions>(options => options.ConfigureConnectionConfigs = _ => hookCalls++);
        services.AddSaasMySqlConnectionConvention();
        services.AddSaasMySqlConnectionConvention();
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<XiHanSqlSugarCoreOptions>>().Value;
        var sqlCalls = 0;
        var mysql = new ConnectionConfig
        {
            DbType = DbType.MySql,
            ConnectionString = "Server=h;Database=d",
            AopEvents = new AopEvents
            {
                OnExecutingChangeSql = (sql, parameters) =>
                {
                    sqlCalls++;
                    return new KeyValuePair<string, SugarParameter[]>(sql, parameters);
                }
            }
        };

        options.ConfigureConnectionConfigs!([mysql]);
        var result = mysql.AopEvents.OnExecutingChangeSql!(
            "SELECT 1",
            [new SugarParameter("@t", new DateTimeOffset(2026, 10, 4, 10, 6, 7, TimeSpan.FromHours(5)))]);

        Assert.Equal(1, hookCalls);
        Assert.Equal(1, sqlCalls);
        Assert.Single(mysql.ConnectionString.Split(';'), part => part.Contains("DateTime", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(Assert.Single(result.Value).Value).Offset);
    }

    private static void InsertTenant(ISqlSugarClient client, long id, TenantDatabaseType databaseType, string connectionString)
    {
        var tenant = new SysTenant
        {
            TenantCode = $"t{id}",
            TenantName = $"租户{id}",
            IsolationMode = TenantIsolationMode.Database,
            DatabaseType = databaseType,
            ConnectionString = connectionString,
            ConfigStatus = TenantConfigStatus.Configured
        };
        SaasTestHelper.SetBasicId(tenant, id);
        client.Insertable(tenant).ExecuteCommand();
    }

    private static SaasTenantConnectionProvider CreateProvider(ISqlSugarClient client)
    {
        var protector = new Mock<ITenantConnectionSecretProtector>();
        protector.Setup(p => p.Unprotect(It.IsAny<string?>())).Returns((string? value) => value);

        var services = new ServiceCollection();
        services.AddSingleton<ICurrentTenant>(new TestCurrentTenant());
        services.AddSingleton<ISqlSugarClientResolver>(new StubClientResolver(client));

        return new SaasTenantConnectionProvider(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            protector.Object,
            Options.Create(new XiHanSqlSugarCoreOptions()));
    }
}
