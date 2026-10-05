# XiHan.BasicApp.Web.Core

## 概述
XiHan.BasicApp.Web.Core 提供基础应用的 Web 基础设施能力，整合 Web Core、Web API、文档、网关、实时通信与 MCP 模块，并提供数据库升级期间的维护模式。

## 核心能力
- Web 基础模块集成与应用初始化入口
- 升级维护模式（`BasicAppUpgradeMaintenanceModeManager` + `MaintenanceModeMiddleware`）
- 接口幂等保护（`[Idempotent]`）

## 架构与职责
- Web 模块聚合：整合基础 Web 能力
- 维护模式：把框架升级引擎的进入/退出映射为进程内标志位，维护期间拦截业务请求

> 数据库初始化（建表 + 种子）由框架 `XiHanDataModule.OnApplicationInitializationAsync` 负责，升级脚本由 Saas 与框架 Upgrade 模块执行，均不在本模块。

## 依赖关系
- `XiHanBasicAppCoreModule`
- `XiHanWebCoreModule`
- `XiHanWebApiModule`
- `XiHanWebDocsModule`
- `XiHanWebRealTimeModule`
- `XiHanWebGatewayModule`
- `XiHanWebMcpModule`

## 接口幂等

在应用服务方法（或整个类）上标 `[Idempotent]`（`XiHan.BasicApp.Core.Attributes`），同一个幂等键的重复请求不会重复执行，而是重播第一次的响应。

### 用法
- `[Idempotent]` 可标在方法或类上，可被继承；控制器与动态 API 都识别方法上的标注和声明该方法的类上的标注，动态 API 以应用服务的方法与类为准。
- 调用方在请求头 `Idempotency-Key` 携带幂等键（头名可由 `HeaderName` 修改）。有效的键非空、长度不超过 `MaxKeyLength`，且只含可见 ASCII 字符（`!` 到 `~`，不含空格）。
- 记录键由租户、调用用户、HTTP 方法、请求路径（转为小写）与键值共同组成，不同用户或租户之间互不影响；仅大小写不同的路径视为同一端点。
- 相同键且请求内容（方法、路径、查询串、动作参数）相同的重复请求，重播首次响应的状态码与响应体，并带响应头 `Idempotency-Replayed: true`。

### 响应语义
| 情形 | 状态码 |
| --- | --- |
| 已完成的相同请求 | 重播首次响应，带 `Idempotency-Replayed: true` |
| 调用方未认证 | 401 |
| 幂等键缺失、无效或超过 `MaxKeyLength` | 400 |
| 表单无法读取 | 400 |
| 含文件或流参数（含上传文件） | 415 |
| 请求内容超过 `MaxRequestBytes` | 413 |
| 相同键的请求正在处理 | 409 |
| 同一键已用于内容不同的请求 | 409 |
| 同一键对应的上次请求结果不确定 | 409，不会自动重试 |
| 幂等存储容量已满 | 503 |

拒绝响应体为 `ApiResponse` 失败结构，状态码同时写入响应码。

### 配置
配置节 `BasicApp:Web:Idempotency`，各上限与时长必须大于零，否则启动校验失败：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `HeaderName` | `Idempotency-Key` | 携带幂等键的请求头名称 |
| `MaxKeyLength` | `128` | 幂等键最大长度（字符） |
| `MaxRequestBytes` | `1048576`（1 MiB） | 参与摘要的请求参数序列化后的最大字节数 |
| `MaxResponseBytes` | `1048576`（1 MiB） | 单个响应快照的最大字节数 |
| `MaxEntries` | `10000` | 进程内存储的最大记录数 |
| `MaxTotalResponseBytes` | `67108864`（64 MiB） | 进程内存储的响应快照总字节上限 |
| `CompletedRetention` | `24:00:00` | 完成记录的保留时长 |
| `ProcessingLease` | `00:05:00` | 事务型端点处理中记录的租约时长，过期后允许重新取得（仅落库存储使用） |
| `PurgeInterval` | `01:00:00` | 过期记录的清理间隔（仅落库存储使用，必须大于零） |

默认存储是进程内的 `DefaultIdempotencyStore`，有容量上限，重启后记录丢失；落库存储见 Saas 模块「接口幂等存储」。

### 过滤器位置
`AddBasicAppIdempotency` 在 MVC 过滤器中以 `XiHanUnitOfWorkFilter` 为锚点插入两层过滤器；找不到该过滤器时启动失败。
- 外层 `IdempotencyFilter` 在工作单元之外：校验、取得幂等键与重播都发生在开启事务之前，重播不会开事务。
- 内层 `IdempotencyCompletionFilter` 在工作单元之内、最贴近动作：动作正常返回后把结果写成快照并完成记录，完成写入登记到当前工作单元；完成写入失败则异常向外传播、工作单元不提交。
- 完成记录与业务写入是否原子取决于存储与业务数据是否在同一连接：进程内存储的完成在工作单元提交成功后才生效；Saas 落库存储的记录在平台库，业务数据也在平台库时二者同一事务提交或回滚，数据库隔离租户的业务数据在租户库，两个事务按顺序分别提交，不是原子提交（见 Saas 模块「接口幂等存储」）。

### 响应处理器
实现 `IIdempotencyResponseProcessor` 并注册到容器（可注册多个，按注册顺序执行），完成过滤器在保存快照之前对 `ObjectResult` 的非空结果值调用 `ProcessAsync` 就地处理；快照保存处理后的值，重播返回该快照。默认不注册任何处理器。处理器抛出异常时完成不写入，异常向外传播、工作单元不提交。

### 动作失败时的行为
- 事务型动作抛出异常：释放幂等键，调用方可用同一键重试。
- 非事务型动作抛出异常：标记为结果不确定，同一键之后返回 409，不会自动重试。
- 动作正常返回但结果无法保存为快照（流结果、超过 `MaxResponseBytes` 或不支持的结果类型）：同样标记为结果不确定。

### 限制
- 只适用于 MVC 控制器与动态 API，不适用于 Minimal API。
- 含文件或流参数的动作会被拒绝（415）。
- 快照只保存 `ObjectResult`（JSON 响应体）、空结果与仅状态码结果：`EmptyResult`、动作结果为 null 与 `StatusCodeResult` 不保存响应体，重播为 200 空响应或对应状态码；`ObjectResult` 的值为 null 时按 JSON `null` 保存，重播响应体为 `null`。
- 已知限制：BasicApp 以 fork 框架源码（`XiHanFun*.slnx` 源码模式）构建时，fork 自带的幂等过滤器也会被注册，直到 fork 移除相应实现为止；fork 的过滤器只识别 fork 自己的幂等标注，对标了 BasicApp `[Idempotent]` 的方法不起作用。

## 数据库升级与维护模式
早期的 `UseAutoVersionUpdate`（`version.txt` 记版本、按 `SnowflakeId:WorkerId == 1` 判主节点）已废除。现行机制：

- **脚本位置**：WebHost 的 `UpdateScripts/<版本>/<版本>.sql`，一个版本一个目录，随输出/发布一同拷贝。
- **执行开关**：`XiHan:Upgrade:EnableAutoCheckOnStartup`（框架缺省 `true`）；关闭时启动不执行脚本，须先手工升级再启动。
- **执行点**：两处都调用 `IUpgradeEngine.ExecuteAsync()`：
  1. Saas 的 `SaasSchemaUpgrader`（`IDbSchemaUpgrader`）在 `DbInitializer` 建表之后、播种之前执行，让存量表的新列先于种子补齐；
  2. 框架 `XiHanUpgradeModule.OnPostApplicationInitializationAsync` 在应用初始化之后再执行一次，前一处已升级时空转。
- **状态记录**：库版本记在 `SysVersion`，每个脚本的执行结果记在 `SysMigrationHistory`；本次从零建出的库直接登记为最新版本，不补跑历史脚本。
- **多节点**：由 `SysVersion` 上的数据库租约锁协调执行权，与 `WorkerId` 无关。
- **失败处理**：引擎返回失败即抛出，中断启动。
- **维护模式**（本模块）：引擎执行期间置位 `MaintenanceModeState`，本节点除 `/health`、`/.well-known/` 外的请求返回 503 并带 `Retry-After: 30`；该状态只在当前进程内生效，多副本统一摘流需在网关或发布编排层处理。

权威说明见 [升级与迁移](../../../../docs/backend/upgrade.md) 与 [UpdateScripts/README.md](../../main/XiHan.BasicApp.WebHost/UpdateScripts/README.md)。

## 使用方式
```csharp
[DependsOn(typeof(XiHanBasicAppWebCoreModule))]
public class MyWebModule : XiHanModule
{
}
```

依赖本模块即可：维护模式中间件在本模块的 `OnApplicationInitialization` 中注册，无需另行调用。

## 目录结构
```text
XiHan.BasicApp.Web.Core/
  README.md
  XiHanBasicAppWebCoreModule.cs
  Idempotency/
    IdempotencyOptions.cs
    IdempotencyFilter.cs
    IdempotencyCompletionFilter.cs
    IdempotencyServiceCollectionExtensions.cs
    DefaultIdempotencyStore.cs
  Upgrade/
    MaintenanceMode.cs
```
