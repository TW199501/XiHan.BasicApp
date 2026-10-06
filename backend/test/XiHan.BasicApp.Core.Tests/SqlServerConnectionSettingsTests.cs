// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.BasicApp.Core.Data;

namespace XiHan.BasicApp.Core.Tests;

/// <summary>
/// SQL Server 连接配置约定测试
/// </summary>
public sealed class SqlServerConnectionSettingsTests
{
    /// <summary>
    /// SQL Server 连接没有 MoreSettings 时新建并开启 nvarchar，连接串不变
    /// </summary>
    [Fact]
    public void Apply_SqlServerWithoutMoreSettings_CreatesSettingsAndEnablesNvarchar()
    {
        var config = new ConnectionConfig { DbType = DbType.SqlServer, ConnectionString = "Server=h;Database=d", MoreSettings = null };

        SqlServerConnectionSettings.Apply(config);

        Assert.NotNull(config.MoreSettings);
        Assert.True(config.MoreSettings.SqlServerCodeFirstNvarchar);
        Assert.Equal("Server=h;Database=d", config.ConnectionString);
    }

    /// <summary>
    /// SQL Server 连接已有 MoreSettings 时沿用原实例，其他设置不变
    /// </summary>
    [Fact]
    public void Apply_SqlServerWithMoreSettings_KeepsInstanceAndOtherValues()
    {
        var settings = new ConnMoreSettings
        {
            IsAutoUpdateQueryFilter = true,
            IsAutoDeleteQueryFilter = true,
            PgSqlIsAutoToLower = false
        };
        var config = new ConnectionConfig { DbType = DbType.SqlServer, ConnectionString = "Server=h", MoreSettings = settings };

        SqlServerConnectionSettings.Apply(config);

        Assert.Same(settings, config.MoreSettings);
        Assert.True(settings.SqlServerCodeFirstNvarchar);
        Assert.True(settings.IsAutoUpdateQueryFilter);
        Assert.True(settings.IsAutoDeleteQueryFilter);
        Assert.False(settings.PgSqlIsAutoToLower);
    }

    /// <summary>
    /// 其他数据库类型不改 MoreSettings
    /// </summary>
    /// <param name="dbType">数据库类型</param>
    [Theory]
    [InlineData(DbType.PostgreSQL)]
    [InlineData(DbType.MySql)]
    [InlineData(DbType.MySqlConnector)]
    [InlineData(DbType.Sqlite)]
    public void Apply_OtherDatabaseTypes_LeavesMoreSettingsUnchanged(DbType dbType)
    {
        var withoutSettings = new ConnectionConfig { DbType = dbType, ConnectionString = "Server=h", MoreSettings = null };
        var settings = new ConnMoreSettings { IsAutoUpdateQueryFilter = true };
        var withSettings = new ConnectionConfig { DbType = dbType, ConnectionString = "Server=h", MoreSettings = settings };

        SqlServerConnectionSettings.Apply(withoutSettings);
        SqlServerConnectionSettings.Apply(withSettings);

        Assert.Null(withoutSettings.MoreSettings);
        Assert.Same(settings, withSettings.MoreSettings);
        Assert.False(settings.SqlServerCodeFirstNvarchar);
        Assert.True(settings.IsAutoUpdateQueryFilter);
    }

    /// <summary>
    /// 重复调用结果相同，沿用同一个 MoreSettings 实例
    /// </summary>
    [Fact]
    public void Apply_Twice_IsIdempotent()
    {
        var config = new ConnectionConfig { DbType = DbType.SqlServer, ConnectionString = "Server=h", MoreSettings = null };

        SqlServerConnectionSettings.Apply(config);
        var settings = config.MoreSettings;
        Assert.NotNull(settings);
        SqlServerConnectionSettings.Apply(config);

        Assert.Same(settings, config.MoreSettings);
        Assert.True(settings.SqlServerCodeFirstNvarchar);
    }

    /// <summary>
    /// 连接配置为 null 时拒绝
    /// </summary>
    [Fact]
    public void Apply_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SqlServerConnectionSettings.Apply(null!));
    }
}
