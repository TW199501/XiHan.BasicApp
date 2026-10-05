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
using XiHan.BasicApp.Saas.Infrastructure.Data;
using XiHan.BasicApp.Saas.Infrastructure.MultiTenancy;
using XiHan.BasicApp.Saas.Tests.Idempotency;
using XiHan.Framework.Data.SqlSugar.Clients;
using XiHan.Framework.Data.SqlSugar.Options;
using XiHan.Framework.MultiTenancy.Abstractions;

namespace XiHan.BasicApp.Saas.Tests;

/// <summary>
/// MySQL 连接串与连接配置的 DateTimeKind 约束测试
/// </summary>
public sealed class MySqlConnectionStringsTests
{
    /// <summary>
    /// 缺少 DateTimeKind 时补上 Utc，其余连接选项保持不变
    /// </summary>
    [Fact]
    public void EnsureUtcDateTimeKind_AddsUtcAndKeepsOtherOptions()
    {
        var result = MySqlConnectionStrings.EnsureUtcDateTimeKind("Server=127.0.0.1;Port=13306;Database=app;Uid=root;Pwd=pw;AllowPublicKeyRetrieval=True;SslMode=None");

        var builder = new MySqlConnectionStringBuilder(result);
        Assert.Equal(MySqlDateTimeKind.Utc, builder.DateTimeKind);
        Assert.Equal("127.0.0.1", builder.Server);
        Assert.Equal(13306u, builder.Port);
        Assert.Equal("app", builder.Database);
        Assert.Equal("root", builder.UserID);
        Assert.True(builder.AllowPublicKeyRetrieval);
        Assert.Equal(MySqlSslMode.None, builder.SslMode);
    }

    /// <summary>
    /// 含特殊字符的口令经规范化后保持原值
    /// </summary>
    [Fact]
    public void EnsureUtcDateTimeKind_PreservesPasswordWithSpecialCharacters()
    {
        var result = MySqlConnectionStrings.EnsureUtcDateTimeKind("Server=h;Database=d;Uid=u;Pwd=\"a;b=c'd\"");

        Assert.Equal("a;b=c'd", new MySqlConnectionStringBuilder(result).Password);
    }

    /// <summary>
    /// 已显式声明 Utc 时原样保留
    /// </summary>
    [Fact]
    public void EnsureUtcDateTimeKind_KeepsExplicitUtc()
    {
        var result = MySqlConnectionStrings.EnsureUtcDateTimeKind("Server=h;Database=d;DateTimeKind=Utc");

        Assert.Equal(MySqlDateTimeKind.Utc, new MySqlConnectionStringBuilder(result).DateTimeKind);
    }

    /// <summary>
    /// 显式声明了非 Utc 的 DateTimeKind 时拒绝，异常信息不含连接串
    /// </summary>
    [Theory]
    [InlineData("Unspecified")]
    [InlineData("Local")]
    public void EnsureUtcDateTimeKind_ThrowsForExplicitNonUtc(string kind)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => MySqlConnectionStrings.EnsureUtcDateTimeKind($"Server=secret-host;Pwd=secret-pwd;DateTimeKind={kind}"));

        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 空白连接串被拒绝
    /// </summary>
    [Fact]
    public void EnsureUtcDateTimeKind_ThrowsForBlank()
    {
        Assert.Throws<ArgumentException>(() => MySqlConnectionStrings.EnsureUtcDateTimeKind(" "));
    }

    /// <summary>
    /// MySQL 连接配置的主库与从库连接串都补上 Utc
    /// </summary>
    [Theory]
    [InlineData(DbType.MySql)]
    [InlineData(DbType.MySqlConnector)]
    public void Apply_NormalizesMainAndSlaveConnectionStrings(DbType dbType)
    {
        var config = new ConnectionConfig
        {
            DbType = dbType,
            ConnectionString = "Server=m;Database=d",
            SlaveConnectionConfigs = [new SlaveConnectionConfig { ConnectionString = "Server=s;Database=d", HitRate = 10 }]
        };

        MySqlConnectionStrings.Apply(config);

        Assert.Equal(MySqlDateTimeKind.Utc, new MySqlConnectionStringBuilder(config.ConnectionString).DateTimeKind);
        var slave = Assert.Single(config.SlaveConnectionConfigs);
        Assert.Equal(MySqlDateTimeKind.Utc, new MySqlConnectionStringBuilder(slave.ConnectionString).DateTimeKind);
        Assert.Equal(10, slave.HitRate);
    }

    /// <summary>
    /// 非 MySQL 的连接配置保持原样
    /// </summary>
    [Fact]
    public void Apply_LeavesOtherDatabaseTypesUnchanged()
    {
        var config = new ConnectionConfig { DbType = DbType.PostgreSQL, ConnectionString = "Host=h;Database=d" };

        MySqlConnectionStrings.Apply(config);

        Assert.Equal("Host=h;Database=d", config.ConnectionString);
    }

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
