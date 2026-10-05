// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using SqlSugar;

namespace XiHan.BasicApp.Saas.Tests.TestDatabases;

/// <summary>
/// 集成测试数据库入口的单元测试
/// </summary>
public sealed class IntegrationDatabaseTests
{
    /// <summary>
    /// 连接配置使用指定的数据库种类、自动关闭连接，并按种类区分 ConfigId
    /// </summary>
    [Theory]
    [InlineData(DbType.PostgreSQL)]
    [InlineData(DbType.SqlServer)]
    [InlineData(DbType.MySql)]
    public void CreateConnectionConfig_UsesDatabaseTypeAndDistinctConfigId(DbType dbType)
    {
        var config = IntegrationDatabase.CreateConnectionConfig(dbType, "Server=127.0.0.1");
        var configId = Assert.IsType<string>(config.ConfigId);

        Assert.Equal(dbType, config.DbType);
        Assert.Equal("Server=127.0.0.1", config.ConnectionString);
        Assert.True(config.IsAutoCloseConnection);
        Assert.Equal(InitKeyType.Attribute, config.InitKeyType);
        Assert.Equal($"IntegrationTest_{dbType}", configId);
    }

    /// <summary>
    /// 未设置或只含空白的环境变量视为未设置
    /// </summary>
    [Fact]
    public void GetConnectionString_ReturnsNullForUnsetOrBlankVariable()
    {
        var variable = $"XIHAN_TEST_E85_{Guid.NewGuid():N}";
        Assert.Null(IntegrationDatabase.GetConnectionString(variable));

        Environment.SetEnvironmentVariable(variable, "  ");
        try
        {
            Assert.Null(IntegrationDatabase.GetConnectionString(variable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// 沿内部异常逐层查找指定类型的异常
    /// </summary>
    [Fact]
    public void FindException_WalksInnerExceptions()
    {
        var inner = new TimeoutException("inner");
        var outer = new InvalidOperationException("outer", new AggregateException(inner));

        Assert.Same(inner, IntegrationDatabase.FindException<TimeoutException>(outer));
        Assert.Same(outer, IntegrationDatabase.FindException<InvalidOperationException>(outer));
        Assert.Null(IntegrationDatabase.FindException<ArgumentException>(outer));
    }
}
