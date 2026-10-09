# 数据库升级脚本

由 `UpgradeEngine` 执行，执行台账记入 `sys_migration_history`。

## 执行时机

数据库初始化分三段：**全部连接建库建表 → 升级脚本 → 播种**（`SaasSchemaUpgrader` 接在框架 `IDbSchemaUpgrader` 上）。

- 建表只建缺失的表、**不改已存在的表**，存量表的新列、新索引只能由脚本补。
- 脚本先于种子执行：种子按最新实体读写，存量表的新列要在种子之前补齐，否则种子一查就撞上不存在的列。
- **新库不跑脚本**：本次启动从零建出全部实体表的平台库、`InitializeDatabase` 新建的库隔离租户独立库，建好即登记为最新脚本版本（`IUpgradeEngine.BaselineAsync`）。
  脚本只在它所属版本之前建的库上执行，按当时的表结构写即可；**不要在脚本里依赖种子数据**。
- 关闭 `XiHan:Upgrade:EnableAutoCheckOnStartup` 时启动不执行脚本，须先手工升级再启动。

## 约定

- **默认只提供PostgreSQL，其他数据库需自行实现**
- **目录名即版本号**：脚本放在 `UpdateScripts/<版本>/` 下，如 `3.10.0/3.10.0.sql`。
  框架的 `FileSystemUpgradeScriptProvider` 只扫**子目录**（`Directory.GetDirectories`），
  同一版本目录内可以放多个 .sql，按文件名升序执行。
- **只有版本号高于库中 `db_version` 的脚本会执行**（记在 `sys_version`，随脚本执行推进）。
  与程序版本 `props/version.props` **无关**——`UpgradeEngine.ExecuteMigrationsAsync` 只比库版本，
  不比较 AppVersion。
- **每个库各跑一遍**：平台库，加上每个已配置完成的库隔离租户的独立库（`SaasUpgradeTenantProvider`）。
  新建的独立库同样登记为最新版本，之后发版的脚本会在它上面执行。
- **独立库只有租户库实体的表**：平台目录、账号与授权、会话令牌、读共享模板等标了 `[PlatformDataSource]` 的实体只在平台库建表。
  改这些表的语句必须先判表存在，否则在独立库上报 `42P01`、中断启动：

  ```sql
  DO $$
  BEGIN
      IF EXISTS (SELECT 1 FROM information_schema.tables
                  WHERE table_schema = current_schema() AND table_name = 'sys_user') THEN
          -- 改 sys_user 的语句
      END IF;
  END
  $$;
  ```
- **公共列一律带下划线**：`basic_id`、`tenant_id`、`created_time`、`is_deleted` 等，所有实体基类（含聚合根）同名；
  5.3.0 之前聚合根的公共列没有声明列名、落库成 `basicid` / `tenantid`，5.3.0 已把存量库改名。
- **标识符一律小写、不加引号。** SqlSugar 建表时未加引号，PostgreSQL 将未加引号的标识符折叠为小写，
  所以库里的实际名是 `sys_oauth_code`、`basic_id`，而不是实体上声明的 `Sys_OAuth_Code`、`Basic_Id`。
  写成 `"Sys_OAuth_Code"` 会因引号带来大小写敏感而报 `42P01 relation does not exist`。
- **写成可重复执行**：用 `IF NOT EXISTS` / `IF EXISTS`。失败的脚本不会记为已执行，下次启动会重试。
- **PostgreSQL 方言**：执行器在 PG 上取事务级建议锁并把本次全部脚本放进同一事务，失败整体回滚。

## 失败会怎样

脚本抛错即整体回滚、写入一条 `Success = false` 的台账，并**中断应用启动** —— 宁可起不来，
也不让应用带着半吊子表结构对外服务。修好脚本后重启即可，失败记录不影响重试。

缺少当前数据库的方言脚本时同样中断启动，但不写台账：读取脚本文件即失败，没有执行任何 SQL。

## 方言目录

升级时按当前连接的数据库类型只执行对应方言的脚本（`DialectAwareUpgradeScriptProvider`），平台库与各库隔离租户的独立库各自判断。

```text
UpdateScripts/
├── 5.6.0/5.6.0.sql            # 根层 = PostgreSQL
└── 5.7.0/
    ├── pgsql/5.7.0.sql        # PostgreSQL
    ├── mssql/5.7.0.sql        # SQL Server
    └── mysql/5.7.0.sql        # MySQL
```

- 版本目录根层的 `.sql` 视为 PostgreSQL 脚本。3.10.0～5.6.0 的历史脚本留在根层，只有 PostgreSQL。
- 同一版本不能同时有根层 `.sql` 与 `pgsql/`；版本目录下只允许 `pgsql/`、`mssql/`、`mysql/` 三个子目录。违反任一条，启动时扫描就失败。
- 自 5.6.1 起每个版本都要有三种方言的脚本（`UpgradeScriptLayoutTests` 检查）：PostgreSQL 放在版本目录根层或 `pgsql/` 均可，`mssql/` 与 `mysql/` 必须有。
- 某版本缺少当前数据库的方言脚本时升级失败并中断启动，不会跳过。该版本之前的版本照常执行并推进 `db_version`；补上脚本后重启，从该版本继续。
- 缺方言导致的失败：升级结果消息是系统的路径不存在异常，如 `升级失败: Could not find a part of the path '…/5.7.0/mssql/__missing__.sql'`；日志中对应的一行是 `执行迁移脚本: 5.7.0/[缺少 mssql 方言脚本]`。两处都带有缺少的版本与方言。
- 新建的库（包括 SQL Server、MySQL）建好即登记为最新版本，不执行历史脚本。
- SQLite、Oracle 等其他数据库没有方言目录：新库照常登记，存量库一旦需要升级就失败。

三种方言的写法：

- 某方言在该版本不需要变更时也要放文件，内容写 `SELECT 1;`。只有注释的文件在 MySQL 上可能报 `Query was empty`。
- SQL Server 脚本不能含 `GO`，整个文件作为一个批次执行；MySQL 脚本不能用 `DELIMITER`。
- MySQL 脚本不能用 `@` 用户变量：连接未开启 `AllowUserVariables`，MySqlConnector 会把它当成未定义的参数报错。需要先查再改时，在脚本里建一个临时存储过程，`CALL` 之后 `DROP`（不需要 `DELIMITER`），数据库账号要有 `CREATE ROUTINE` 权限。
- MySQL 的 DDL 会隐式提交，失败时已执行的语句无法回滚。三种方言都写成可重复执行：PostgreSQL 用 `IF NOT EXISTS` / `DO $$ … $$`，SQL Server 用 `IF OBJECT_ID(N'…', N'U') IS NULL`、`IF COL_LENGTH(N'…', N'…') IS NULL`，MySQL 用 `CREATE TABLE IF NOT EXISTS`，或在临时存储过程里先查 `information_schema` 再执行。
- 脚本在驱动默认的命令超时（30 秒）内执行。表数据量大时 `ALTER` 可能超时并中断启动，重启后通常仍会超时；这类库先手动执行该版本脚本，再启动。

## SQL Server 字符串列

新建的 SQL Server 表字符串列为 `nvarchar`（连接开启 `SqlServerCodeFirstNvarchar`）。5.6.1 的 `mssql/5.6.1.sql` 在收件箱去重键修补之后，把既有库 `dbo` 架构下 `Sys_` 开头的表（含分表）的 `varchar` 列转为 `nvarchar`，长度、排序规则、可空性与索引定义不变，重跑不做改动。之后的 SQL Server 脚本新增字符串列一律写 `nvarchar(n)`。

- 转换在一个事务内完成，失败整段回滚；去重键修补在它之前单独提交。
- 列被外键、默认值约束、检查约束、架构绑定视图、手工统计信息、主键或唯一约束等引用，或长度超过 4000、转换后索引键超过上限时，脚本报错（错误号 50011）并列出表名、列名与对象，不做改动；处理后重启即从 5.6.1 重跑。
- 原先已存成 `?` 的字符无法还原。
- 逐列重写整张表，大表可能超过 30 秒命令超时，先手动执行该脚本再启动。
