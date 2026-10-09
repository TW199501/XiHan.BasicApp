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
    /// <param name="option">DateTimeKind 选项的写法</param>
    [Theory]
    [InlineData("DateTimeKind=Utc")]
    [InlineData("datetimekind=utc")]
    [InlineData("DateTime Kind=Utc")]
    public void EnsureUtcDateTimeKind_KeepsExplicitUtc(string option)
    {
        var result = MySqlConnectionStrings.EnsureUtcDateTimeKind($"Server=h;Database=d;{option}");

        var builder = new MySqlConnectionStringBuilder(result);
        Assert.Equal(MySqlDateTimeKind.Utc, builder.DateTimeKind);
        Assert.Equal(3, builder.Count);
    }

    /// <summary>
    /// 显式声明了非 Utc 的 DateTimeKind 时拒绝，异常信息不含连接串
    /// </summary>
    /// <param name="option">DateTimeKind 选项的写法</param>
    [Theory]
    [InlineData("DateTimeKind=Unspecified")]
    [InlineData("DateTimeKind=Local")]
    [InlineData("datetimekind=local")]
    [InlineData("DateTime Kind=Local")]
    public void EnsureUtcDateTimeKind_ThrowsForExplicitNonUtc(string option)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => MySqlConnectionStrings.EnsureUtcDateTimeKind($"Server=secret-host;Pwd=secret-pwd;{option}"));

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
        Assert.Null(config.AopEvents?.OnExecutingChangeSql);
    }

    /// <summary>
    /// MySQL 连接执行前把 DateTimeOffset 参数转成 UTC，时刻不变，其他参数保持原样
    /// </summary>
    [Fact]
    public void Apply_ConvertsDateTimeOffsetParametersToUtc()
    {
        var config = new ConnectionConfig { DbType = DbType.MySql, ConnectionString = "Server=h;Database=d" };
        var localTime = new DateTimeOffset(2026, 10, 4, 13, 6, 7, 123, TimeSpan.FromHours(8));
        var otherTime = new DateTimeOffset(2026, 10, 4, 10, 6, 7, 123, TimeSpan.FromHours(5));
        var utcTime = new DateTimeOffset(2026, 10, 4, 5, 6, 7, 123, TimeSpan.Zero);
        var dateTime = new DateTime(2026, 10, 4, 5, 6, 7, DateTimeKind.Unspecified);

        MySqlConnectionStrings.Apply(config);
        var result = config.AopEvents!.OnExecutingChangeSql!(
            "SELECT 1",
            [
                new SugarParameter("@a", localTime),
                new SugarParameter("@b", otherTime),
                new SugarParameter("@c", utcTime),
                new SugarParameter("@d", dateTime),
                new SugarParameter("@e", "x"),
                new SugarParameter("@f", null)
            ]);

        Assert.Equal("SELECT 1", result.Key);
        var values = result.Value.Select(parameter => parameter.Value).ToArray();
        Assert.All(values.Take(3), value => Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(value).Offset));
        Assert.Equal(localTime.UtcDateTime, ((DateTimeOffset)values[0]).UtcDateTime);
        Assert.Equal(otherTime.UtcDateTime, ((DateTimeOffset)values[1]).UtcDateTime);
        Assert.Equal(utcTime, values[2]);
        Assert.Equal(dateTime, values[3]);
        Assert.Equal("x", values[4]);
        Assert.Null(values[5]);
    }

    /// <summary>
    /// 已有的执行前钩子先执行，其返回的语句与参数再做 UTC 转换
    /// </summary>
    [Fact]
    public void Apply_ChainsExistingExecutingChangeSql()
    {
        var calls = 0;
        var added = new DateTimeOffset(2026, 10, 4, 10, 6, 7, TimeSpan.FromHours(5));
        var config = new ConnectionConfig
        {
            DbType = DbType.MySqlConnector,
            ConnectionString = "Server=h;Database=d",
            AopEvents = new AopEvents
            {
                OnExecutingChangeSql = (sql, parameters) =>
                {
                    calls++;
                    return new KeyValuePair<string, SugarParameter[]>(sql + " /*hooked*/", [.. parameters, new SugarParameter("@added", added)]);
                }
            }
        };

        MySqlConnectionStrings.Apply(config);
        var result = config.AopEvents.OnExecutingChangeSql!("SELECT 1", []);

        Assert.Equal(1, calls);
        Assert.Equal("SELECT 1 /*hooked*/", result.Key);
        var value = Assert.IsType<DateTimeOffset>(Assert.Single(result.Value).Value);
        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal(added.UtcDateTime, value.UtcDateTime);
    }

    /// <summary>
    /// 同一连接配置规范化两次，连接串只有一个 DateTimeKind，已有钩子每次执行只调用一次
    /// </summary>
    [Fact]
    public void Apply_Twice_IsIdempotent()
    {
        var calls = 0;
        var config = new ConnectionConfig
        {
            DbType = DbType.MySql,
            ConnectionString = "Server=h;Database=d",
            AopEvents = new AopEvents
            {
                OnExecutingChangeSql = (sql, parameters) =>
                {
                    calls++;
                    return new KeyValuePair<string, SugarParameter[]>(sql, parameters);
                }
            }
        };

        MySqlConnectionStrings.Apply(config);
        var first = config.AopEvents.OnExecutingChangeSql;
        MySqlConnectionStrings.Apply(config);
        config.AopEvents.OnExecutingChangeSql!("SELECT 1", []);

        Assert.Same(first, config.AopEvents.OnExecutingChangeSql);
        Assert.Equal(1, calls);
        Assert.Single(config.ConnectionString.Split(';'), part => part.Contains("DateTime", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 客户端构建时另设其他 AOP 事件，不影响已挂上的执行前钩子
    /// </summary>
    [Fact]
    public void Apply_HookSurvivesClientAopConfiguration()
    {
        var config = new ConnectionConfig { ConfigId = "main", DbType = DbType.MySql, ConnectionString = "Server=h;Database=d" };
        MySqlConnectionStrings.Apply(config);
        var hook = config.AopEvents!.OnExecutingChangeSql;

        var scope = new SqlSugarScope(config, client =>
        {
            var provider = client.GetConnectionScope("main");
            provider.Aop.OnLogExecuting = (_, _) => { };
            provider.Aop.OnLogExecuted = (_, _) => { };
            provider.Aop.OnError = _ => { };
            provider.Aop.DataExecuting = (_, _) => { };
        });

        Assert.Same(hook, scope.GetConnectionScope("main").CurrentConnectionConfig.AopEvents.OnExecutingChangeSql);
    }

    /// <summary>
    /// 非 MySQL 连接不包装实体写入事件
    /// </summary>
    [Fact]
    public void ApplyDataExecuting_NonMySql_LeavesDataExecutingUnchanged()
    {
        Action<object, DataFilterModel> existing = (_, _) => { };
        var config = new ConnectionConfig
        {
            DbType = DbType.PostgreSQL,
            ConnectionString = "Host=h;Database=d",
            AopEvents = new AopEvents { DataExecuting = existing }
        };

        MySqlConnectionStrings.ApplyDataExecuting(config);

        Assert.Same(existing, config.AopEvents.DataExecuting);
    }

    /// <summary>
    /// MySQL 连接插入与更新实体时，带非 0 偏移的 DateTimeOffset 列值转成 UTC，时刻不变，其他列保持原样
    /// </summary>
    [Theory]
    [InlineData(DataFilterType.InsertByObject)]
    [InlineData(DataFilterType.UpdateByObject)]
    public void ApplyDataExecuting_ConvertsDateTimeOffsetColumnsToUtc(DataFilterType operationType)
    {
        var config = new ConnectionConfig { DbType = DbType.MySql, ConnectionString = "Server=h;Database=d" };
        var time = new DateTimeOffset(2026, 10, 4, 10, 6, 7, 123, TimeSpan.FromHours(5));
        var nullableTime = new DateTimeOffset(2026, 10, 4, 13, 6, 7, 123, TimeSpan.FromHours(8));
        var plain = new DateTime(2026, 10, 4, 10, 6, 7, DateTimeKind.Unspecified);
        var entity = new TimeEntity { Time = time, NullableTime = nullableTime, EmptyTime = null, Plain = plain };

        MySqlConnectionStrings.ApplyDataExecuting(config);
        InvokeForAllColumns(config, entity, operationType);

        Assert.Equal(TimeSpan.Zero, entity.Time.Offset);
        Assert.Equal(time.UtcDateTime, entity.Time.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, entity.NullableTime!.Value.Offset);
        Assert.Equal(nullableTime.UtcDateTime, entity.NullableTime.Value.UtcDateTime);
        Assert.Null(entity.EmptyTime);
        Assert.Equal(plain, entity.Plain);
    }

    /// <summary>
    /// 按对象删除时不转换
    /// </summary>
    [Fact]
    public void ApplyDataExecuting_DeleteByObject_LeavesValues()
    {
        var config = new ConnectionConfig { DbType = DbType.MySql, ConnectionString = "Server=h;Database=d" };
        var time = new DateTimeOffset(2026, 10, 4, 10, 6, 7, 123, TimeSpan.FromHours(5));
        var entity = new TimeEntity { Time = time };

        MySqlConnectionStrings.ApplyDataExecuting(config);
        InvokeForAllColumns(config, entity, DataFilterType.DeleteByObject);

        Assert.Equal(TimeSpan.FromHours(5), entity.Time.Offset);
    }

    /// <summary>
    /// 已有的实体写入委派先执行，其写入的值再做 UTC 转换
    /// </summary>
    [Fact]
    public void ApplyDataExecuting_RunsExistingDelegateFirst()
    {
        var calls = 0;
        var assigned = new DateTimeOffset(2026, 10, 4, 10, 6, 7, 123, TimeSpan.FromHours(5));
        var config = new ConnectionConfig
        {
            DbType = DbType.MySqlConnector,
            ConnectionString = "Server=h;Database=d",
            AopEvents = new AopEvents
            {
                DataExecuting = (_, entityInfo) =>
                {
                    calls++;
                    ((TimeEntity)entityInfo.EntityValue).Time = assigned;
                }
            }
        };
        var entity = new TimeEntity { Time = DateTimeOffset.UnixEpoch };

        MySqlConnectionStrings.ApplyDataExecuting(config);
        config.AopEvents.DataExecuting!(entity.Time, CreateFilterModel(entity, nameof(TimeEntity.Time), DataFilterType.InsertByObject));

        Assert.Equal(1, calls);
        Assert.Equal(TimeSpan.Zero, entity.Time.Offset);
        Assert.Equal(assigned.UtcDateTime, entity.Time.UtcDateTime);
    }

    /// <summary>
    /// 同一连接配置包装两次只挂一层，已有委派每次执行只调用一次
    /// </summary>
    [Fact]
    public void ApplyDataExecuting_Twice_WrapsOnce()
    {
        var calls = 0;
        var config = new ConnectionConfig
        {
            DbType = DbType.MySql,
            ConnectionString = "Server=h;Database=d",
            AopEvents = new AopEvents { DataExecuting = (_, _) => calls++ }
        };
        var entity = new TimeEntity();

        MySqlConnectionStrings.ApplyDataExecuting(config);
        var first = config.AopEvents.DataExecuting;
        MySqlConnectionStrings.ApplyDataExecuting(config);
        config.AopEvents.DataExecuting!(entity.Time, CreateFilterModel(entity, nameof(TimeEntity.Time), DataFilterType.InsertByObject));

        Assert.Same(first, config.AopEvents.DataExecuting);
        Assert.Equal(1, calls);
    }

    private static void InvokeForAllColumns(ConnectionConfig config, TimeEntity entity, DataFilterType operationType)
    {
        foreach (var property in typeof(TimeEntity).GetProperties())
        {
            config.AopEvents!.DataExecuting!(property.GetValue(entity)!, CreateFilterModel(entity, property.Name, operationType));
        }
    }

    private static DataFilterModel CreateFilterModel(TimeEntity entity, string propertyName, DataFilterType operationType)
    {
        var property = typeof(TimeEntity).GetProperty(propertyName)!;
        return new DataFilterModel
        {
            OperationType = operationType,
            EntityValue = entity,
            EntityColumnInfo = new EntityColumnInfo { PropertyInfo = property, PropertyName = property.Name, DbColumnName = property.Name }
        };
    }

    /// <summary>
    /// 带时间列的测试实体
    /// </summary>
    private sealed class TimeEntity
    {
        /// <summary>
        /// 时间
        /// </summary>
        public DateTimeOffset Time { get; set; }

        /// <summary>
        /// 可空时间
        /// </summary>
        public DateTimeOffset? NullableTime { get; set; }

        /// <summary>
        /// 为空的可空时间
        /// </summary>
        public DateTimeOffset? EmptyTime { get; set; }

        /// <summary>
        /// 不带偏移的时间
        /// </summary>
        public DateTime Plain { get; set; }
    }
}
