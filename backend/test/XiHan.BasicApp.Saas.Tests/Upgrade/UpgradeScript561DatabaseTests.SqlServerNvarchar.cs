// Copyright (c) 2021-Present XiHanFun and contributors.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Reflection;
using SqlSugar;
using XiHan.BasicApp.AI.Domain.Entities;
using XiHan.BasicApp.Chat.Domain.Entities;
using XiHan.BasicApp.CodeGeneration.Domain.Entities;
using XiHan.BasicApp.Printing.Domain.Entities;
using XiHan.BasicApp.Saas.Domain.Entities;
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
