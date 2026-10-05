// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.EventBus;

namespace XiHan.BasicApp.Saas.Tests.EventBox;

/// <summary>
/// 收发件箱 CodeFirst 列定义惯例测试
/// </summary>
public sealed class SaasEventBoxCodeFirstConventionTests
{
    /// <summary>
    /// MySQL 与 SQL Server 的去重键使用区分大小写的二进制排序规则
    /// </summary>
    [Theory]
    [InlineData(DbType.MySql, "varchar(256) COLLATE utf8mb4_bin")]
    [InlineData(DbType.SqlServer, "varchar(256) COLLATE SQL_Latin1_General_CP1_CS_AS")]
    public void Apply_SetsCaseSensitiveDedupKeyColumnType(DbType dbType, string expected)
    {
        var config = NewConfig(dbType);

        SaasEventBoxCodeFirstConvention.Apply(config);

        var column = GetColumn(config, nameof(SysEventInbox.DedupKey));
        Assert.Equal(expected, column.DataType);
        Assert.Equal(0, column.Length);
    }

    /// <summary>
    /// SQL Server 连接开启 nvarchar 建表时，去重键也用 nvarchar
    /// </summary>
    [Fact]
    public void Apply_UsesNvarcharWhenSqlServerCodeFirstNvarchar()
    {
        var config = NewConfig(DbType.SqlServer);
        config.MoreSettings = new ConnMoreSettings { SqlServerCodeFirstNvarchar = true };

        SaasEventBoxCodeFirstConvention.Apply(config);

        Assert.Equal("nvarchar(256) COLLATE SQL_Latin1_General_CP1_CS_AS", GetColumn(config, nameof(SysEventInbox.DedupKey)).DataType);
    }

    /// <summary>
    /// PostgreSQL 的去重键沿用默认定义
    /// </summary>
    [Fact]
    public void Apply_LeavesPostgresDedupKeyUnchanged()
    {
        var config = NewConfig(DbType.PostgreSQL);

        SaasEventBoxCodeFirstConvention.Apply(config);

        var column = GetColumn(config, nameof(SysEventInbox.DedupKey));
        Assert.True(string.IsNullOrEmpty(column.DataType));
        Assert.Equal(256, column.Length);
    }

    /// <summary>
    /// 已挂接的实体服务仍会执行，且先于本惯例执行
    /// </summary>
    [Fact]
    public void Apply_KeepsExistingEntityService()
    {
        var config = NewConfig(DbType.MySql);
        config.ConfigureExternalServices = new ConfigureExternalServices
        {
            EntityService = (property, column) =>
            {
                if (property.Name == nameof(SysEventInbox.EventName))
                {
                    column.ColumnDescription = "existing";
                }
            }
        };

        SaasEventBoxCodeFirstConvention.Apply(config);

        Assert.Equal("existing", GetColumn(config, nameof(SysEventInbox.EventName)).ColumnDescription);
        Assert.Equal("varchar(256) COLLATE utf8mb4_bin", GetColumn(config, nameof(SysEventInbox.DedupKey)).DataType);
    }

    private static ConnectionConfig NewConfig(DbType dbType)
    {
        return new ConnectionConfig
        {
            ConfigId = $"convention-{Guid.NewGuid():N}",
            ConnectionString = "Server=127.0.0.1",
            DbType = dbType,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute
        };
    }

    private static EntityColumnInfo GetColumn(ConnectionConfig config, string propertyName)
    {
        using var client = new SqlSugarClient(config);
        return client.EntityMaintenance.GetEntityInfo<SysEventInbox>().Columns.Single(column => column.PropertyName == propertyName);
    }
}
