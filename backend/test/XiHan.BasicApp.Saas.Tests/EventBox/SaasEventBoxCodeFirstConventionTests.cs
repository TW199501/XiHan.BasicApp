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
    /// MySQL 的去重键使用 utf8mb4_bin
    /// </summary>
    [Fact]
    public void Apply_SetsMySqlDedupKeyColumnType()
    {
        var config = NewConfig(DbType.MySql);

        SaasEventBoxCodeFirstConvention.Apply(config);

        var column = GetColumn(config, nameof(SysEventInbox.DedupKey));
        Assert.Equal("varchar(256) COLLATE utf8mb4_bin", column.DataType);
        Assert.Equal(0, column.Length);
    }

    /// <summary>
    /// SQL Server 的去重键使用 nvarchar 与数据库默认排序规则对应的区分大小写版本，数据库默认排序规则只读取一次
    /// </summary>
    [Fact]
    public void Apply_SetsSqlServerDedupKeyColumnTypeFromDatabaseCollation()
    {
        var config = NewConfig(DbType.SqlServer);
        var reads = 0;

        SaasEventBoxCodeFirstConvention.Apply(config, readConfig =>
        {
            Assert.Same(config, readConfig);
            reads++;
            return "Chinese_PRC_CI_AS";
        });

        var column = GetColumn(config, nameof(SysEventInbox.DedupKey));
        Assert.Equal("nvarchar(256) COLLATE Chinese_PRC_CS_AS", column.DataType);
        Assert.Equal(0, column.Length);
        Assert.Equal("nvarchar(256) COLLATE Chinese_PRC_CS_AS", GetColumn(config, nameof(SysEventInbox.DedupKey)).DataType);
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// 读取数据库默认排序规则失败后，下一次映射会重新读取
    /// </summary>
    [Fact]
    public void Apply_RetriesDatabaseCollationAfterReadFailure()
    {
        var config = NewConfig(DbType.SqlServer);
        var reads = 0;
        SaasEventBoxCodeFirstConvention.Apply(config, _ =>
        {
            reads++;
            return reads == 1 ? throw new InvalidOperationException("unreachable") : "SQL_Latin1_General_CP1_CI_AS";
        });

        Assert.ThrowsAny<Exception>(() => GetColumn(config, nameof(SysEventInbox.DedupKey)));

        Assert.Equal("nvarchar(256) COLLATE SQL_Latin1_General_CP1_CS_AS", GetColumn(config, nameof(SysEventInbox.DedupKey)).DataType);
        Assert.Equal(2, reads);
    }

    /// <summary>
    /// 不是 SQL Server 时不读取数据库默认排序规则
    /// </summary>
    [Theory]
    [InlineData(DbType.MySql)]
    [InlineData(DbType.PostgreSQL)]
    public void Apply_DoesNotReadDatabaseCollationOutsideSqlServer(DbType dbType)
    {
        var config = NewConfig(dbType);

        SaasEventBoxCodeFirstConvention.Apply(config, _ => throw new InvalidOperationException("should not read"));

        _ = GetColumn(config, nameof(SysEventInbox.DedupKey));
    }

    /// <summary>
    /// 区分大小写版本：CI 换成 CS、AI 换成 AS，其余部分不变
    /// </summary>
    [Theory]
    [InlineData("SQL_Latin1_General_CP1_CI_AS", "SQL_Latin1_General_CP1_CS_AS")]
    [InlineData("Chinese_PRC_CI_AS", "Chinese_PRC_CS_AS")]
    [InlineData("Latin1_General_100_CI_AI_SC_UTF8", "Latin1_General_100_CS_AS_SC_UTF8")]
    [InlineData("Latin1_General_100_CI_AS_KS_WS", "Latin1_General_100_CS_AS_KS_WS")]
    [InlineData("SQL_Latin1_General_CP1_CS_AS", "SQL_Latin1_General_CP1_CS_AS")]
    [InlineData("Latin1_General_100_BIN2", "Latin1_General_100_BIN2")]
    public void ToCaseSensitiveCollation_ReplacesInsensitiveParts(string collation, string expected)
    {
        Assert.Equal(expected, SaasEventBoxCodeFirstConvention.ToCaseSensitiveCollation(collation));
    }

    /// <summary>
    /// 既没有大小写部分也不是二进制排序规则时报错
    /// </summary>
    [Fact]
    public void ToCaseSensitiveCollation_RejectsUnknownCollation()
    {
        Assert.Throws<ArgumentException>(() => SaasEventBoxCodeFirstConvention.ToCaseSensitiveCollation("Latin1_General"));
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
