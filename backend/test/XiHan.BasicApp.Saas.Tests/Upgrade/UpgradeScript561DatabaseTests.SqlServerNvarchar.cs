// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Reflection;
using Microsoft.Data.SqlClient;
using SqlSugar;
using XiHan.BasicApp.AI.Domain.Entities;
using XiHan.BasicApp.Chat.Domain.Entities;
using XiHan.BasicApp.CodeGeneration.Domain.Entities;
using XiHan.BasicApp.Printing.Domain.Entities;
using XiHan.BasicApp.Saas.Domain.Entities;
using XiHan.BasicApp.Saas.Infrastructure.Upgrade;
using XiHan.BasicApp.Saas.Tests.TestDatabases;
using XiHan.BasicApp.Workflow.Domain.Entities;
using XiHan.Framework.Domain.Entities.Abstracts;
using DataRow = System.Data.DataRow;

namespace XiHan.BasicApp.Saas.Tests.Upgrade;

/// <summary>
/// SQL Server 字符串列为 nvarchar：新建库的全部实体，以及 5.6.1 脚本对既有库 varchar 列的转换
/// </summary>
public sealed partial class UpgradeScript561DatabaseTests
{
    private const string SqlServerDefaultCollation = "SQL_Latin1_General_CP1_CI_AS";

    private static readonly Assembly[] EntityAssemblies =
    [
        typeof(SysPosition).Assembly,
        typeof(SysAiAssistant).Assembly,
        typeof(SysChatConversation).Assembly,
        typeof(SysCodeGenDataSource).Assembly,
        typeof(SysPrintTemplate).Assembly,
        typeof(SysWorkflowBookmark).Assembly
    ];

    [Fact]
    public async Task SqlServer_新建库全部实体字符串列为nvarchar_中文读回一致()
    {
        await using var database = await SqlServerScratchDatabase.CreateAsync(RequireConnectionString(IntegrationDatabase.SqlServerVariable), SqlServerDefaultCollation);
        using var db = CreateClient(DbType.SqlServer, database.ConnectionString);

        var entityTypes = InitAllEntityTables(db);

        var columns = await ReadSqlServerSysColumnsAsync(db);
        Assert.Equal(entityTypes.Count, columns.Select(column => column.Table).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(columns, column => column.Type == "varchar");
        Assert.Contains(columns, column => column is { Table: "Sys_Position", Column: "Position_Code", Type: "nvarchar", CharLength: 100, IsNullable: false });

        await InsertPositionAsync(db, 1, "岗位-1", "岗位一", "备注：中文");
        string[] expectedRows = ["1|岗位-1|岗位一|备注：中文"];
        Assert.Equal(expectedRows, await ReadPositionRowsAsync(db));
    }

    private const string LegacyExtraObjectsSql = """
        CREATE TABLE dbo.Ext_E116 (Id int NOT NULL, V varchar(10) NULL);
        CREATE TABLE dbo.SysE116 (Id int NOT NULL, V varchar(10) NULL);
        CREATE TABLE dbo.Sys_E116_Max (Id bigint NOT NULL PRIMARY KEY, Body varchar(max) NULL, Code varchar(50) NOT NULL, Remark varchar(100) NULL, Note varchar(100) NULL);
        CREATE UNIQUE NONCLUSTERED INDEX UX_Sys_E116_Max_Code ON dbo.Sys_E116_Max (Code DESC) INCLUDE (Body) WHERE Code <> ''
            WITH (FILLFACTOR = 80, PAD_INDEX = ON, DATA_COMPRESSION = PAGE);
        CREATE UNIQUE NONCLUSTERED INDEX UX_Sys_E116_Max_Remark ON dbo.Sys_E116_Max (Remark) WITH (IGNORE_DUP_KEY = ON);
        INSERT INTO dbo.Sys_E116_Max (Id, Body, Code, Remark, Note) VALUES (1, 'body', 'c-1', 'r-1', 'n-1');
        """;

    [Fact]
    public async Task SqlServer_旗标false的既有库_varchar列转nvarchar_其余定义与数据不变_再次执行不做改动()
    {
        await using var database = await SqlServerScratchDatabase.CreateAsync(RequireConnectionString(IntegrationDatabase.SqlServerVariable), SqlServerDefaultCollation);
        using (var legacy = CreateVarcharLegacyClient(database.ConnectionString))
        {
            _ = InitAllEntityTables(legacy);
            await InsertPositionAsync(legacy, 1, "P-1", "岗位一", null);
            await InsertPositionAsync(legacy, 2, "P-2", "Position Two", "remark");
            _ = await legacy.Ado.ExecuteCommandAsync(LegacyExtraObjectsSql);
            _ = await legacy.Ado.GetIntAsync("SELECT COUNT(*) FROM dbo.Sys_E116_Max WHERE Note = 'x'");
        }

        using var db = CreateClient(DbType.SqlServer, database.ConnectionString);
        var columnsBefore = await ReadSqlServerSysColumnsAsync(db);
        var indexesBefore = await ReadSqlServerSysIndexesAsync(db);
        var rowsBefore = await ReadPositionRowsAsync(db);
        var otherColumnsBefore = await ReadSqlServerOtherColumnsAsync(db);
        Assert.Contains(columnsBefore, column => column is { Table: "Sys_Position", Column: "Position_Code", Type: "varchar", CharLength: 100, IsNullable: false });
        Assert.Contains(columnsBefore, column => column is { Table: "Sys_E116_Max", Column: "Body", Type: "varchar", CharLength: -1 });
        Assert.Contains(indexesBefore, index => index.StartsWith("Sys_Position|UX_Sys_Position_TeId_PoCo|1|", StringComparison.Ordinal));
        Assert.Contains(indexesBefore, index => index.StartsWith("Sys_E116_Max|UX_Sys_E116_Max_Code|1|0|NONCLUSTERED|([Code]<>'')|0|80|1|PAGE|PRIMARY|Code-,Body+", StringComparison.Ordinal));
        string[] expectedRows = ["1|P-1|???|<null>", "2|P-2|Position Two|remark"];
        Assert.Equal(expectedRows, rowsBefore);
        Assert.True(await db.Ado.GetIntAsync("SELECT COUNT(*) FROM sys.stats WHERE object_id = OBJECT_ID(N'dbo.Sys_E116_Max') AND auto_created = 1") > 0);
        Assert.All(otherColumnsBefore, column => Assert.Contains("|varchar|", column, StringComparison.Ordinal));

        await ExecuteScriptAsync(db, UpgradeScriptDialect.SqlServer);

        var columnsAfter = await ReadSqlServerSysColumnsAsync(db);
        Assert.Equal(columnsBefore.Select(column => column.Type == "varchar" ? column with { Type = "nvarchar" } : column), columnsAfter);
        Assert.Equal(indexesBefore, await ReadSqlServerSysIndexesAsync(db));
        Assert.Equal(rowsBefore, await ReadPositionRowsAsync(db));
        Assert.Equal(otherColumnsBefore, await ReadSqlServerOtherColumnsAsync(db));
        var modifiedAfterFirstRun = await ReadSqlServerSysModifyDatesAsync(db);

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await ExecuteScriptAsync(db, UpgradeScriptDialect.SqlServer);

        Assert.Equal(columnsAfter, await ReadSqlServerSysColumnsAsync(db));
        Assert.Equal(indexesBefore, await ReadSqlServerSysIndexesAsync(db));
        Assert.Equal(rowsBefore, await ReadPositionRowsAsync(db));
        Assert.Equal(modifiedAfterFirstRun, await ReadSqlServerSysModifyDatesAsync(db));

        await InsertPositionAsync(db, 3, "岗位-3", "岗位三", "中文备注");
        Assert.Contains("3|岗位-3|岗位三|中文备注", await ReadPositionRowsAsync(db));
        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() => InsertPositionAsync(db, 4, "P-1", "重复", null));
        Assert.Equal(2601, IntegrationDatabase.FindException<SqlException>(duplicate)?.Number);
    }

    [Theory]
    [InlineData("CREATE VIEW dbo.V_E116 WITH SCHEMABINDING AS SELECT Basic_Id, Position_Name FROM dbo.Sys_Position", "Sys_Position.Position_Name 架构绑定引用 dbo.V_E116")]
    [InlineData("CREATE TABLE dbo.Ext_E116_Code (Code varchar(500) NOT NULL PRIMARY KEY); ALTER TABLE dbo.Sys_Position ADD CONSTRAINT FK_Sys_Position_E116 FOREIGN KEY (Remark) REFERENCES dbo.Ext_E116_Code (Code);", "Sys_Position.Remark 外键 FK_Sys_Position_E116")]
    [InlineData("ALTER TABLE dbo.Sys_Position ADD CONSTRAINT DF_Sys_Position_E116 DEFAULT ('x') FOR Remark;", "Sys_Position.Remark 默认值约束 DF_Sys_Position_E116")]
    [InlineData("CREATE STATISTICS ST_Sys_Position_E116 ON dbo.Sys_Position (Remark);", "Sys_Position.Remark 手工统计信息 ST_Sys_Position_E116")]
    [InlineData("CREATE TABLE dbo.Sys_E116_Long (Id bigint NOT NULL PRIMARY KEY, Body varchar(5000) NULL);", "Sys_E116_Long.Body 长度超过 4000")]
    [InlineData("CREATE TABLE dbo.Sys_E116_Key (Id bigint NOT NULL PRIMARY KEY, Code varchar(1000) NULL); CREATE INDEX IX_Sys_E116_Key_Code ON dbo.Sys_E116_Key (Code);", "Sys_E116_Key.Code 转换后键长超过上限的索引 IX_Sys_E116_Key_Code")]
    [InlineData("CREATE TABLE dbo.Sys_E116_Pk (Code varchar(50) NOT NULL CONSTRAINT PK_Sys_E116_Pk PRIMARY KEY);", "Sys_E116_Pk.Code 主键 PK_Sys_E116_Pk")]
    public async Task SqlServer_varchar列有不支持的依赖_报错并列出_库不变(string setupSql, string expectedBlocker)
    {
        await using var database = await SqlServerScratchDatabase.CreateAsync(RequireConnectionString(IntegrationDatabase.SqlServerVariable), SqlServerDefaultCollation);
        using (var legacy = CreateVarcharLegacyClient(database.ConnectionString))
        {
            legacy.CodeFirst.InitTables(typeof(SysPosition));
            await InsertPositionAsync(legacy, 1, "P-1", "Position One", null);
            _ = await legacy.Ado.ExecuteCommandAsync(setupSql);
        }

        using var db = CreateClient(DbType.SqlServer, database.ConnectionString);
        var columnsBefore = await ReadSqlServerSysColumnsAsync(db);
        var indexesBefore = await ReadSqlServerSysIndexesAsync(db);
        var rowsBefore = await ReadPositionRowsAsync(db);
        var modifiedBefore = await ReadSqlServerSysModifyDatesAsync(db);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteScriptAsync(db, UpgradeScriptDialect.SqlServer));

        var sqlException = IntegrationDatabase.FindException<SqlException>(exception);
        Assert.NotNull(sqlException);
        Assert.Equal(50011, sqlException.Number);
        Assert.StartsWith("Sys_ 表的 varchar 列存在不支持的依赖，脚本未做改动：", sqlException.Message, StringComparison.Ordinal);
        Assert.Contains(expectedBlocker, sqlException.Message, StringComparison.Ordinal);
        Assert.Equal(columnsBefore, await ReadSqlServerSysColumnsAsync(db));
        Assert.Equal(indexesBefore, await ReadSqlServerSysIndexesAsync(db));
        Assert.Equal(rowsBefore, await ReadPositionRowsAsync(db));
        Assert.Equal(modifiedBefore, await ReadSqlServerSysModifyDatesAsync(db));
    }

    private static SqlSugarClient CreateVarcharLegacyClient(string connectionString)
    {
        var config = IntegrationDatabase.CreateConnectionConfig(DbType.SqlServer, connectionString);
        config.ConfigId = $"E116_legacy_{Guid.NewGuid():N}";
        config.MoreSettings.SqlServerCodeFirstNvarchar = false;
        return new SqlSugarClient(config);
    }

    private static async Task<List<string>> ReadSqlServerSysIndexesAsync(ISqlSugarClient db)
    {
        var indexes = await db.Ado.SqlQueryAsync<string>(
            "SELECT CONCAT(tb.name, N'|', i.name, N'|', i.is_unique, N'|', i.is_primary_key, N'|', i.type_desc, N'|', ISNULL(i.filter_definition, N''), N'|', " +
            "i.ignore_dup_key, N'|', i.fill_factor, N'|', i.is_padded, N'|', p.data_compression_desc, N'|', ds.name, N'|', " +
            "(SELECT STRING_AGG(c.name + CASE WHEN ic.is_included_column = 1 THEN N'+' WHEN ic.is_descending_key = 1 THEN N'-' ELSE N'' END COLLATE DATABASE_DEFAULT, N',') " +
            "WITHIN GROUP (ORDER BY ic.is_included_column, ic.key_ordinal, ic.index_column_id) " +
            "FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
            "WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id)) COLLATE DATABASE_DEFAULT " +
            "FROM sys.indexes AS i " +
            "JOIN sys.tables AS tb ON tb.object_id = i.object_id " +
            "JOIN sys.partitions AS p ON p.object_id = i.object_id AND p.index_id = i.index_id AND p.partition_number = 1 " +
            "JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id " +
            "WHERE tb.schema_id = SCHEMA_ID(N'dbo') AND tb.name LIKE N'Sys[_]%' AND i.type > 0");
        Assert.NotEmpty(indexes);
        return [.. indexes.Order(StringComparer.Ordinal)];
    }

    private static async Task<List<string>> ReadSqlServerOtherColumnsAsync(ISqlSugarClient db)
    {
        var columns = await db.Ado.SqlQueryAsync<string>(
            "SELECT CONCAT(tb.name, N'.', c.name, N'|', t.name, N'|', c.max_length) COLLATE DATABASE_DEFAULT " +
            "FROM sys.columns AS c JOIN sys.tables AS tb ON tb.object_id = c.object_id JOIN sys.types AS t ON t.user_type_id = c.user_type_id " +
            "WHERE tb.name IN (N'Ext_E116', N'SysE116') AND t.name IN (N'varchar', N'nvarchar')");
        Assert.Equal(2, columns.Count);
        return [.. columns.Order(StringComparer.Ordinal)];
    }

    private static async Task<List<string>> ReadSqlServerSysModifyDatesAsync(ISqlSugarClient db)
    {
        var dates = await db.Ado.SqlQueryAsync<string>(
            "SELECT CONCAT(name, N'|', CONVERT(varchar(30), modify_date, 121)) COLLATE DATABASE_DEFAULT " +
            "FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo') AND name LIKE N'Sys[_]%'");
        return [.. dates.Order(StringComparer.Ordinal)];
    }

    private static List<Type> InitAllEntityTables(ISqlSugarClient db)
    {
        var entityTypes = EntityAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && type.GetCustomAttribute<SugarTable>(true) is not null
                && typeof(IEntityBase).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();
        Assert.All(EntityAssemblies, assembly => Assert.Contains(entityTypes, type => type.Assembly == assembly));

        foreach (var entityType in entityTypes)
        {
            if (entityType.GetCustomAttribute<SplitTableAttribute>(true) is not null)
            {
                db.CodeFirst.SplitTables().InitTables(entityType);
            }
            else
            {
                db.CodeFirst.InitTables(entityType);
            }
        }

        return entityTypes;
    }

    private static async Task InsertPositionAsync(ISqlSugarClient db, long id, string code, string name, string? remark)
    {
        var position = new SysPosition { PositionCode = code, PositionName = name, Remark = remark };
        SaasTestHelper.SetBasicId(position, id);
        _ = await db.Insertable(position).ExecuteCommandAsync();
    }

    private static async Task<List<string>> ReadPositionRowsAsync(ISqlSugarClient db)
    {
        return await db.Ado.SqlQueryAsync<string>(
            "SELECT CONCAT(Basic_Id, N'|', Position_Code, N'|', Position_Name, N'|', ISNULL(Remark, N'<null>')) FROM Sys_Position ORDER BY Basic_Id");
    }

    private static async Task<List<SqlServerColumn>> ReadSqlServerSysColumnsAsync(ISqlSugarClient db)
    {
        var data = await db.Ado.GetDataTableAsync(
            "SELECT tb.name AS table_name, c.name AS column_name, t.name AS type_name, " +
            "CASE WHEN c.max_length = -1 THEN -1 WHEN t.name IN (N'nvarchar', N'nchar') THEN c.max_length / 2 ELSE c.max_length END AS char_length, " +
            "c.collation_name AS collation_name, c.is_nullable AS is_nullable " +
            "FROM sys.columns AS c " +
            "JOIN sys.tables AS tb ON tb.object_id = c.object_id " +
            "JOIN sys.types AS t ON t.user_type_id = c.user_type_id " +
            "WHERE tb.schema_id = SCHEMA_ID(N'dbo') AND tb.name LIKE N'Sys[_]%'");
        var columns = new List<SqlServerColumn>();
        foreach (DataRow row in data.Rows)
        {
            columns.Add(new SqlServerColumn(
                Text(row, "table_name")!,
                Text(row, "column_name")!,
                Text(row, "type_name")!,
                Convert.ToInt32(row["char_length"], CultureInfo.InvariantCulture),
                Text(row, "collation_name"),
                Convert.ToBoolean(row["is_nullable"], CultureInfo.InvariantCulture)));
        }

        Assert.NotEmpty(columns);
        return [.. columns.OrderBy(column => column.Table, StringComparer.Ordinal).ThenBy(column => column.Column, StringComparer.Ordinal)];
    }

    /// <summary>
    /// SQL Server 列定义
    /// </summary>
    /// <param name="Table">表名</param>
    /// <param name="Column">列名</param>
    /// <param name="Type">类型名，如 nvarchar</param>
    /// <param name="CharLength">字符长度；max 为 -1，非字符列为字节长度</param>
    /// <param name="Collation">排序规则；非字符列为 null</param>
    /// <param name="IsNullable">是否可为 NULL</param>
    private sealed record SqlServerColumn(string Table, string Column, string Type, int CharLength, string? Collation, bool IsNullable);
}
