// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using MySqlConnector;
using SqlSugar;
using XiHan.BasicApp.Core.Data;

namespace XiHan.BasicApp.Core.Tests;

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
}
