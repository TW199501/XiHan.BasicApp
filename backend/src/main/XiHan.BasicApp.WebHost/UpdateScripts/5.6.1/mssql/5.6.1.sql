-- SQL Server：收件箱去重键 Sys_Event_Inbox.Dedup_Key 改为 nvarchar，使用区分大小写、区分重音的排序规则。
--
-- 目标排序规则由数据库默认排序规则推导，与 SaasEventBoxCodeFirstConvention.GetSqlServerDedupKeyCollationCandidates 一致：
-- 按下划线分段，CI 换成 CS、AI 换成 AS，去掉原有的 KS、WS，得到 <base>_CS_AS；在 AS 之后插入 KS_WS，得到 <base>_CS_AS_KS_WS。
-- sys.fn_helpcollations() 中有 <base>_CS_AS_KS_WS 时用它，否则用 <base>_CS_AS，两者都没有时报错，不做改动；二进制排序规则原样使用。
-- 列改为 nvarchar，字符长度与可空性保持原样；包含该列的索引先删除，改列后按原名、原定义重建。
-- 重建时保留键列顺序与 ASC/DESC、INCLUDE 列、筛选条件、唯一性、主键/唯一约束、聚集与否，
-- 以及 IGNORE_DUP_KEY、FILLFACTOR、PAD_INDEX、ALLOW_ROW_LOCKS、ALLOW_PAGE_LOCKS、DATA_COMPRESSION 与文件组。
-- 表或列不存在、列已是 nvarchar 且排序规则已是目标值时不做任何改动，可重复执行。
-- 该列不是 varchar/nvarchar、长度超过 4000，或被外键、检查约束、计算列、手工统计信息、非行存储索引、分区索引引用时报错，不做改动。
-- 原列为 varchar 时，写入时无法用该代码页表示的字符已存成 ?，转换后仍是 ?，无法恢复。
-- 整个脚本作为一个批次执行，改动在一个事务内完成；开头的 SET 选项满足筛选索引的建立要求，与客户端的默认设置无关。

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @tableName sysname = N'Sys_Event_Inbox';
DECLARE @columnName sysname = N'Dedup_Key';
DECLARE @databaseCollation sysname = CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation'));
DECLARE @segments nvarchar(300) = N'_' + @databaseCollation + N'_';
DECLARE @preferredCollation sysname;
DECLARE @fallbackCollation sysname;
DECLARE @targetCollation sysname;

IF CHARINDEX(N'_BIN_', @segments) > 0 OR CHARINDEX(N'_BIN2_', @segments) > 0
BEGIN
    SET @preferredCollation = @databaseCollation;
    SET @fallbackCollation = @databaseCollation;
END
ELSE
BEGIN
    SET @segments = REPLACE(REPLACE(REPLACE(REPLACE(@segments, N'_CI_', N'_CS_'), N'_AI_', N'_AS_'), N'_KS_', N'_'), N'_WS_', N'_');
    SET @fallbackCollation = SUBSTRING(@segments, 2, LEN(@segments) - 2);
    SET @segments = REPLACE(@segments, N'_AS_', N'_AS_KS_WS_');
    SET @preferredCollation = SUBSTRING(@segments, 2, LEN(@segments) - 2);
END;

SELECT @targetCollation = CASE
        WHEN EXISTS (SELECT 1 FROM sys.fn_helpcollations() WHERE name = @preferredCollation) THEN @preferredCollation
        WHEN EXISTS (SELECT 1 FROM sys.fn_helpcollations() WHERE name = @fallbackCollation) THEN @fallbackCollation
    END;

DECLARE @objectId int = OBJECT_ID(QUOTENAME(@tableName), N'U');
DECLARE @columnId int;
DECLARE @currentCollation sysname;
DECLARE @typeName sysname;
DECLARE @maxLength smallint;
DECLARE @isNullable bit;

IF @objectId IS NOT NULL
BEGIN
    SELECT @columnId = c.column_id,
           @currentCollation = c.collation_name,
           @typeName = t.name,
           @maxLength = c.max_length,
           @isNullable = c.is_nullable
    FROM sys.columns AS c
    JOIN sys.types AS t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = @objectId AND c.name = @columnName;
END;

IF @columnId IS NOT NULL AND (@typeName <> N'nvarchar' OR @targetCollation IS NULL OR @currentCollation <> @targetCollation)
BEGIN
    IF @targetCollation IS NULL
    BEGIN
        THROW 50001, N'由数据库默认排序规则推导出的区分大小写排序规则不存在，脚本未做改动。', 1;
    END;

    DECLARE @characterLength int =
        CASE
            WHEN @maxLength = -1 THEN -1
            WHEN @typeName = N'nvarchar' THEN @maxLength / 2
            WHEN @typeName = N'varchar' THEN @maxLength
        END;

    IF @characterLength IS NULL OR @characterLength = -1 OR @characterLength > 4000
    BEGIN
        THROW 50002, N'Sys_Event_Inbox.Dedup_Key 不是长度不超过 4000 的 varchar/nvarchar 列，脚本未做改动。', 1;
    END;

    IF EXISTS (SELECT 1 FROM sys.foreign_key_columns
               WHERE (parent_object_id = @objectId AND parent_column_id = @columnId)
                  OR (referenced_object_id = @objectId AND referenced_column_id = @columnId))
        OR EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE parent_object_id = @objectId
                     AND (parent_column_id = @columnId OR CHARINDEX(QUOTENAME(@columnName), definition) > 0))
        OR EXISTS (SELECT 1 FROM sys.computed_columns
                   WHERE object_id = @objectId AND CHARINDEX(QUOTENAME(@columnName), definition) > 0)
        OR EXISTS (SELECT 1 FROM sys.stats AS s
                   JOIN sys.stats_columns AS sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id
                   WHERE s.object_id = @objectId AND sc.column_id = @columnId AND s.user_created = 1)
        OR EXISTS (SELECT 1 FROM sys.indexes AS i
                   JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                   JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
                   WHERE i.object_id = @objectId AND ic.column_id = @columnId AND (i.type NOT IN (1, 2) OR ds.type <> 'FG'))
    BEGIN
        THROW 50003, N'Sys_Event_Inbox.Dedup_Key 被外键、检查约束、计算列、手工统计信息、非行存储索引或分区索引引用，脚本未做改动。', 1;
    END;

    DECLARE @qualifiedTable nvarchar(600) = QUOTENAME(OBJECT_SCHEMA_NAME(@objectId)) + N'.' + QUOTENAME(@tableName);
    DECLARE @alterColumn nvarchar(max) =
        N'ALTER TABLE ' + @qualifiedTable + N' ALTER COLUMN ' + QUOTENAME(@columnName) +
        N' nvarchar(' + CONVERT(nvarchar(10), @characterLength) + N') COLLATE ' + @targetCollation +
        CASE WHEN @isNullable = 1 THEN N' NULL;' ELSE N' NOT NULL;' END;

    DECLARE @dropIndexes nvarchar(max);
    DECLARE @createIndexes nvarchar(max);

    WITH affected AS (
        SELECT i.index_id, i.name, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.filter_definition,
               N' WITH (PAD_INDEX = ' + CASE WHEN i.is_padded = 1 THEN N'ON' ELSE N'OFF' END +
               CASE WHEN i.fill_factor > 0 THEN N', FILLFACTOR = ' + CONVERT(nvarchar(3), i.fill_factor) ELSE N'' END +
               CASE WHEN i.is_unique = 1 THEN N', IGNORE_DUP_KEY = ' + CASE WHEN i.ignore_dup_key = 1 THEN N'ON' ELSE N'OFF' END ELSE N'' END +
               N', ALLOW_ROW_LOCKS = ' + CASE WHEN i.allow_row_locks = 1 THEN N'ON' ELSE N'OFF' END +
               N', ALLOW_PAGE_LOCKS = ' + CASE WHEN i.allow_page_locks = 1 THEN N'ON' ELSE N'OFF' END +
               N', DATA_COMPRESSION = ' + p.data_compression_desc COLLATE DATABASE_DEFAULT +
               N') ON ' + QUOTENAME(ds.name) AS storage_options
        FROM sys.indexes AS i
        JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
        JOIN sys.partitions AS p ON p.object_id = i.object_id AND p.index_id = i.index_id AND p.partition_number = 1
        WHERE i.object_id = @objectId
          AND EXISTS (SELECT 1 FROM sys.index_columns AS ic
                      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.column_id = @columnId)
    ),
    definitions AS (
        SELECT a.*,
               (SELECT STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(c.name) + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N' ASC' END) COLLATE DATABASE_DEFAULT, N', ')
                           WITHIN GROUP (ORDER BY ic.key_ordinal)
                FROM sys.index_columns AS ic
                JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = @objectId AND ic.index_id = a.index_id AND ic.is_included_column = 0) AS key_columns,
               (SELECT STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(c.name)) COLLATE DATABASE_DEFAULT, N', ') WITHIN GROUP (ORDER BY ic.index_column_id)
                FROM sys.index_columns AS ic
                JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = @objectId AND ic.index_id = a.index_id AND ic.is_included_column = 1) AS included_columns
        FROM affected AS a
    )
    SELECT
        @dropIndexes = STRING_AGG(CONVERT(nvarchar(max),
            CASE WHEN is_primary_key = 1 OR is_unique_constraint = 1
                 THEN N'ALTER TABLE ' + @qualifiedTable + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'
                 ELSE N'DROP INDEX ' + QUOTENAME(name) + N' ON ' + @qualifiedTable + N';'
            END) COLLATE DATABASE_DEFAULT, NCHAR(10)),
        @createIndexes = STRING_AGG(CONVERT(nvarchar(max),
            CASE WHEN is_primary_key = 1 OR is_unique_constraint = 1
                 THEN N'ALTER TABLE ' + @qualifiedTable + N' ADD CONSTRAINT ' + QUOTENAME(name) +
                      CASE WHEN is_primary_key = 1 THEN N' PRIMARY KEY ' ELSE N' UNIQUE ' END + type_desc +
                      N' (' + key_columns + N')' + storage_options + N';'
                 ELSE N'CREATE ' + CASE WHEN is_unique = 1 THEN N'UNIQUE ' ELSE N'' END + type_desc +
                      N' INDEX ' + QUOTENAME(name) + N' ON ' + @qualifiedTable + N' (' + key_columns + N')' +
                      ISNULL(N' INCLUDE (' + included_columns + N')', N'') +
                      ISNULL(N' WHERE ' + filter_definition, N'') + storage_options + N';'
            END) COLLATE DATABASE_DEFAULT, NCHAR(10))
    FROM definitions;

    BEGIN TRANSACTION;

    IF @dropIndexes IS NOT NULL
        EXEC sys.sp_executesql @dropIndexes;

    EXEC sys.sp_executesql @alterColumn;

    IF @createIndexes IS NOT NULL
        EXEC sys.sp_executesql @createIndexes;

    COMMIT TRANSACTION;
END;

-- SQL Server：dbo 架构下名称以 Sys_ 开头的用户表（含分表），varchar 列改为 nvarchar。
--
-- 列从系统目录动态查出，不写死表名；没有 varchar 列时不做任何改动，可重复执行。
-- 每列改为 nvarchar，字符长度、排序规则与可空性保持原样；varchar(max) 改为 nvarchar(max)。
-- 引用这些列的行存储索引先删除，改列后按原名、原定义重建：唯一性、聚集与否、键列顺序与 ASC/DESC、INCLUDE 列、筛选条件，
-- 以及 IGNORE_DUP_KEY、FILLFACTOR、PAD_INDEX、ALLOW_ROW_LOCKS、ALLOW_PAGE_LOCKS、DATA_COMPRESSION 与文件组。
-- 以下情况报错并列出表名、列名与对象类型，不做改动：长度超过 4000 的 varchar、别名类型、计算列、动态数据掩码、Always Encrypted、
-- 内存优化表、时态表、CDC 跟踪表、外键、默认值约束、检查约束、被计算列引用、架构绑定引用、手工统计信息、全文索引、
-- 主键、唯一约束、非行存储索引、分区索引、已禁用索引，以及转换后键长超过上限（聚集 900、非聚集 1700 字节）的索引。
-- 原列为 varchar 时，写入时无法用该代码页表示的字符已存成 ?，转换后仍是 ?，无法恢复。
-- 本段在一个事务内完成；与上一段（去重键修补）各自提交。

DECLARE @nvColumns TABLE (
    object_id int NOT NULL,
    column_id int NOT NULL,
    table_name sysname NOT NULL,
    column_name sysname NOT NULL,
    max_length smallint NOT NULL,
    collation_name sysname NOT NULL,
    is_nullable bit NOT NULL,
    PRIMARY KEY (object_id, column_id)
);
DECLARE @nvBlockers TABLE (description nvarchar(1000) NOT NULL);
DECLARE @nvIndexes TABLE (
    object_id int NOT NULL,
    index_id int NOT NULL,
    index_type tinyint NOT NULL,
    drop_sql nvarchar(max) NOT NULL,
    create_sql nvarchar(max) NOT NULL,
    PRIMARY KEY (object_id, index_id)
);
DECLARE @nvMessage nvarchar(2048);
DECLARE @nvDropIndexes nvarchar(max);
DECLARE @nvAlterColumns nvarchar(max);
DECLARE @nvCreateIndexes nvarchar(max);

INSERT INTO @nvColumns (object_id, column_id, table_name, column_name, max_length, collation_name, is_nullable)
SELECT c.object_id, c.column_id, tb.name, c.name, c.max_length, c.collation_name, c.is_nullable
FROM sys.columns AS c
JOIN sys.tables AS tb ON tb.object_id = c.object_id
WHERE tb.schema_id = SCHEMA_ID(N'dbo')
  AND tb.is_ms_shipped = 0
  AND tb.name LIKE N'Sys[_]%'
  AND c.system_type_id = TYPE_ID(N'varchar');

IF EXISTS (SELECT 1 FROM @nvColumns)
BEGIN
    INSERT INTO @nvBlockers (description)
    SELECT n.table_name + N'.' + n.column_name + N' ' + b.object_type + ISNULL(N' ' + b.object_name COLLATE DATABASE_DEFAULT, N'')
    FROM @nvColumns AS n
    CROSS APPLY (
        SELECT N'长度超过 4000', CAST(NULL AS nvarchar(300)) WHERE n.max_length > 4000
        UNION ALL
        SELECT N'列属性', CASE
                   WHEN c.user_type_id <> c.system_type_id THEN N'别名类型 ' + TYPE_NAME(c.user_type_id)
                   WHEN c.is_computed = 1 THEN N'计算列'
                   WHEN c.is_masked = 1 THEN N'动态数据掩码'
                   WHEN c.encryption_type IS NOT NULL THEN N'Always Encrypted'
               END
        FROM sys.columns AS c
        WHERE c.object_id = n.object_id AND c.column_id = n.column_id
          AND (c.user_type_id <> c.system_type_id OR c.is_computed = 1 OR c.is_masked = 1 OR c.encryption_type IS NOT NULL)
        UNION ALL
        SELECT N'表类型', CASE
                   WHEN tb.is_memory_optimized = 1 THEN N'内存优化表'
                   WHEN tb.temporal_type <> 0 THEN N'时态表'
                   ELSE N'CDC 跟踪表'
               END
        FROM sys.tables AS tb
        WHERE tb.object_id = n.object_id AND (tb.is_memory_optimized = 1 OR tb.temporal_type <> 0 OR tb.is_tracked_by_cdc = 1)
        UNION ALL
        SELECT N'外键', fk.name
        FROM sys.foreign_key_columns AS fkc
        JOIN sys.foreign_keys AS fk ON fk.object_id = fkc.constraint_object_id
        WHERE (fkc.parent_object_id = n.object_id AND fkc.parent_column_id = n.column_id)
           OR (fkc.referenced_object_id = n.object_id AND fkc.referenced_column_id = n.column_id)
        UNION ALL
        SELECT N'默认值约束', dc.name
        FROM sys.default_constraints AS dc
        WHERE dc.parent_object_id = n.object_id AND dc.parent_column_id = n.column_id
        UNION ALL
        SELECT N'检查约束', cc.name
        FROM sys.check_constraints AS cc
        WHERE cc.parent_object_id = n.object_id
          AND (cc.parent_column_id = n.column_id OR CHARINDEX(QUOTENAME(n.column_name), cc.definition) > 0)
        UNION ALL
        SELECT N'被计算列引用', cc.name
        FROM sys.computed_columns AS cc
        WHERE cc.object_id = n.object_id AND cc.column_id <> n.column_id AND CHARINDEX(QUOTENAME(n.column_name), cc.definition) > 0
        UNION ALL
        SELECT N'架构绑定引用', OBJECT_SCHEMA_NAME(d.referencing_id) + N'.' + OBJECT_NAME(d.referencing_id)
        FROM sys.sql_expression_dependencies AS d
        WHERE d.referenced_id = n.object_id AND d.referenced_minor_id = n.column_id AND d.is_schema_bound_reference = 1
          AND d.referencing_id <> n.object_id
        UNION ALL
        SELECT N'手工统计信息', s.name
        FROM sys.stats AS s
        JOIN sys.stats_columns AS sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id
        WHERE s.object_id = n.object_id AND sc.column_id = n.column_id AND s.user_created = 1
        UNION ALL
        SELECT N'全文索引', NULL
        FROM sys.fulltext_index_columns AS f
        WHERE f.object_id = n.object_id AND f.column_id = n.column_id
        UNION ALL
        SELECT CASE
                   WHEN i.is_primary_key = 1 THEN N'主键'
                   WHEN i.is_unique_constraint = 1 THEN N'唯一约束'
                   WHEN i.type NOT IN (1, 2) THEN N'非行存储索引'
                   WHEN ds.type <> 'FG' THEN N'分区索引'
                   WHEN i.is_disabled = 1 THEN N'已禁用索引'
                   ELSE N'转换后键长超过上限的索引'
               END, i.name
        FROM sys.indexes AS i
        JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
        WHERE i.object_id = n.object_id AND ic.column_id = n.column_id
          AND (i.is_primary_key = 1 OR i.is_unique_constraint = 1 OR i.type NOT IN (1, 2) OR ds.type <> 'FG' OR i.is_disabled = 1
               OR (SELECT SUM(CASE WHEN kc.system_type_id = TYPE_ID(N'varchar') THEN kc.max_length * 2 ELSE kc.max_length END)
                   FROM sys.index_columns AS kic
                   JOIN sys.columns AS kc ON kc.object_id = kic.object_id AND kc.column_id = kic.column_id
                   WHERE kic.object_id = i.object_id AND kic.index_id = i.index_id AND kic.is_included_column = 0)
                  > CASE WHEN i.type = 1 THEN 900 ELSE 1700 END)
    ) AS b(object_type, object_name);

    IF EXISTS (SELECT 1 FROM @nvBlockers)
    BEGIN
        SELECT @nvMessage = LEFT(
            N'Sys_ 表的 varchar 列存在不支持的依赖，脚本未做改动：' +
            STRING_AGG(CONVERT(nvarchar(max), description), N'；') WITHIN GROUP (ORDER BY description), 2000)
        FROM @nvBlockers;
        THROW 50011, @nvMessage, 1;
    END;

    WITH affected AS (
        SELECT DISTINCT ic.object_id, ic.index_id
        FROM sys.index_columns AS ic
        JOIN @nvColumns AS n ON n.object_id = ic.object_id AND n.column_id = ic.column_id
    )
    INSERT INTO @nvIndexes (object_id, index_id, index_type, drop_sql, create_sql)
    SELECT i.object_id, i.index_id, i.type,
           N'DROP INDEX ' + QUOTENAME(i.name) + N' ON ' + q.qualified_table + N';',
           N'CREATE ' + CASE WHEN i.is_unique = 1 THEN N'UNIQUE ' ELSE N'' END + i.type_desc COLLATE DATABASE_DEFAULT +
           N' INDEX ' + QUOTENAME(i.name) + N' ON ' + q.qualified_table + N' (' + k.key_columns + N')' +
           ISNULL(N' INCLUDE (' + inc.included_columns + N')', N'') +
           ISNULL(N' WHERE ' + i.filter_definition, N'') +
           N' WITH (PAD_INDEX = ' + CASE WHEN i.is_padded = 1 THEN N'ON' ELSE N'OFF' END +
           CASE WHEN i.fill_factor > 0 THEN N', FILLFACTOR = ' + CONVERT(nvarchar(3), i.fill_factor) ELSE N'' END +
           CASE WHEN i.is_unique = 1 THEN N', IGNORE_DUP_KEY = ' + CASE WHEN i.ignore_dup_key = 1 THEN N'ON' ELSE N'OFF' END ELSE N'' END +
           N', ALLOW_ROW_LOCKS = ' + CASE WHEN i.allow_row_locks = 1 THEN N'ON' ELSE N'OFF' END +
           N', ALLOW_PAGE_LOCKS = ' + CASE WHEN i.allow_page_locks = 1 THEN N'ON' ELSE N'OFF' END +
           N', DATA_COMPRESSION = ' + p.data_compression_desc COLLATE DATABASE_DEFAULT +
           N') ON ' + QUOTENAME(ds.name) + N';'
    FROM affected AS a
    JOIN sys.indexes AS i ON i.object_id = a.object_id AND i.index_id = a.index_id
    JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
    JOIN sys.partitions AS p ON p.object_id = i.object_id AND p.index_id = i.index_id AND p.partition_number = 1
    CROSS APPLY (SELECT QUOTENAME(OBJECT_SCHEMA_NAME(i.object_id)) + N'.' + QUOTENAME(OBJECT_NAME(i.object_id)) AS qualified_table) AS q
    CROSS APPLY (
        SELECT STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(c.name) + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N' ASC' END) COLLATE DATABASE_DEFAULT, N', ')
                   WITHIN GROUP (ORDER BY ic.key_ordinal) AS key_columns
        FROM sys.index_columns AS ic
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
    ) AS k
    CROSS APPLY (
        SELECT STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(c.name)) COLLATE DATABASE_DEFAULT, N', ')
                   WITHIN GROUP (ORDER BY ic.index_column_id) AS included_columns
        FROM sys.index_columns AS ic
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
    ) AS inc
    WHERE i.type IN (1, 2);

    SELECT @nvDropIndexes = STRING_AGG(drop_sql, NCHAR(10)) WITHIN GROUP (ORDER BY index_type DESC, object_id, index_id)
    FROM @nvIndexes;

    SELECT @nvCreateIndexes = STRING_AGG(create_sql, NCHAR(10)) WITHIN GROUP (ORDER BY index_type, object_id, index_id)
    FROM @nvIndexes;

    SELECT @nvAlterColumns = STRING_AGG(CONVERT(nvarchar(max),
               N'ALTER TABLE ' + QUOTENAME(N'dbo') + N'.' + QUOTENAME(table_name) + N' ALTER COLUMN ' + QUOTENAME(column_name) +
               N' nvarchar(' + CASE WHEN max_length = -1 THEN N'max' ELSE CONVERT(nvarchar(10), max_length) END + N') COLLATE ' + collation_name +
               CASE WHEN is_nullable = 1 THEN N' NULL;' ELSE N' NOT NULL;' END) COLLATE DATABASE_DEFAULT, NCHAR(10))
               WITHIN GROUP (ORDER BY table_name, column_id)
    FROM @nvColumns;

    BEGIN TRANSACTION;

    IF @nvDropIndexes IS NOT NULL
        EXEC sys.sp_executesql @nvDropIndexes;

    EXEC sys.sp_executesql @nvAlterColumns;

    IF @nvCreateIndexes IS NOT NULL
        EXEC sys.sp_executesql @nvCreateIndexes;

    COMMIT TRANSACTION;
END;
