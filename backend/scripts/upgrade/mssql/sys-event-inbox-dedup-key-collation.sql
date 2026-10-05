-- SQL Server：收件箱去重键 Sys_Event_Inbox.Dedup_Key 改为 nvarchar，使用区分大小写、区分重音的排序规则。
--
-- 目标排序规则由数据库默认排序规则推导：按下划线分段，CI 换成 CS、AI 换成 AS，其余分段不变，
-- 与 SaasEventBoxCodeFirstConvention.ToCaseSensitiveCollation 一致；推导出的排序规则不存在时报错，不做改动。
-- 列改为 nvarchar，字符长度与可空性保持原样；包含该列的索引先删除，改列后按原名、原定义重建。
-- 表或列不存在、列已是 nvarchar 且排序规则已是目标值时不做任何改动，可重复执行。
-- 该列不是 varchar/nvarchar、长度超过 4000，或被外键、检查约束、计算列、手工统计信息、非行存储索引引用时报错，不做改动。
-- 原列为 varchar 时，写入时无法用该代码页表示的字符已存成 ?，转换后仍是 ?，无法恢复。
-- 整个脚本作为一个批次执行，改动在一个事务内完成。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @tableName sysname = N'Sys_Event_Inbox';
DECLARE @columnName sysname = N'Dedup_Key';
DECLARE @databaseCollation sysname = CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation'));
DECLARE @derivedCollation nvarchar(260) =
    REPLACE(REPLACE(N'_' + @databaseCollation + N'_', N'_CI_', N'_CS_'), N'_AI_', N'_AS_');
DECLARE @targetCollation sysname = SUBSTRING(@derivedCollation, 2, LEN(@derivedCollation) - 2);

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

IF @columnId IS NOT NULL AND (@typeName <> N'nvarchar' OR @currentCollation <> @targetCollation)
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.fn_helpcollations() WHERE name = @targetCollation)
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
                   WHERE i.object_id = @objectId AND ic.column_id = @columnId AND i.type NOT IN (1, 2))
    BEGIN
        THROW 50003, N'Sys_Event_Inbox.Dedup_Key 被外键、检查约束、计算列、手工统计信息或非行存储索引引用，脚本未做改动。', 1;
    END;

    DECLARE @qualifiedTable nvarchar(600) = QUOTENAME(OBJECT_SCHEMA_NAME(@objectId)) + N'.' + QUOTENAME(@tableName);
    DECLARE @alterColumn nvarchar(max) =
        N'ALTER TABLE ' + @qualifiedTable + N' ALTER COLUMN ' + QUOTENAME(@columnName) +
        N' nvarchar(' + CONVERT(nvarchar(10), @characterLength) + N') COLLATE ' + @targetCollation +
        CASE WHEN @isNullable = 1 THEN N' NULL;' ELSE N' NOT NULL;' END;

    DECLARE @dropIndexes nvarchar(max);
    DECLARE @createIndexes nvarchar(max);

    WITH affected AS (
        SELECT i.index_id, i.name, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.filter_definition
        FROM sys.indexes AS i
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
                      N' (' + key_columns + N');'
                 ELSE N'CREATE ' + CASE WHEN is_unique = 1 THEN N'UNIQUE ' ELSE N'' END + type_desc +
                      N' INDEX ' + QUOTENAME(name) + N' ON ' + @qualifiedTable + N' (' + key_columns + N')' +
                      ISNULL(N' INCLUDE (' + included_columns + N')', N'') +
                      ISNULL(N' WHERE ' + filter_definition, N'') + N';'
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
