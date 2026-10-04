# XiHan.BasicApp.Web.Core

## 概述
XiHan.BasicApp.Web.Core 提供基础应用的 Web 基础设施能力，整合 Web Core、Web API、文档、网关与实时通信模块，并提供自动版本脚本更新扩展。

## 核心能力
- Web 基础模块集成与应用初始化入口
- 自动版本脚本更新扩展（`UseAutoVersionUpdate`）
- 接口幂等保护（`[Idempotent]`）

## 架构与职责
- Web 模块聚合：整合基础 Web 能力
- Web 扩展：提供统一的应用构建扩展方法

> 数据库初始化（建表 + 种子）由框架 `XiHanDataModule.OnApplicationInitializationAsync` 负责，不在本模块。

## 依赖关系
- `XiHanBasicAppCoreModule`
- `XiHanWebCoreModule`
- `XiHanWebApiModule`
- `XiHanWebDocsModule`
- `XiHanWebRealTimeModule`
- `XiHanWebGatewayModule`

## 接口幂等

在应用服务方法（或整个类）上标 `[Idempotent]`（`XiHan.BasicApp.Core.Attributes`），同一个幂等键的重复请求不会重复执行，而是重播第一次的响应。

### 用法
- `[Idempotent]` 可标在方法或类上，可被继承；动态 API 以应用服务方法上的标注为准。
- 调用方在请求头 `Idempotency-Key` 携带幂等键（头名可由 `HeaderName` 修改）。键按租户、调用用户、HTTP 方法、请求路径与键值共同区分，不同用户或租户之间互不影响。
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

默认存储是进程内的 `DefaultIdempotencyStore`，有容量上限，重启后记录丢失；落库存储见 Saas 模块「接口幂等存储」。

### 过滤器位置
`AddBasicAppIdempotency` 在 MVC 过滤器中以 `XiHanUnitOfWorkFilter` 为锚点插入两层过滤器；找不到该过滤器时启动失败。
- 外层 `IdempotencyFilter` 在工作单元之外：校验、取得幂等键与重播都发生在开启事务之前，重播不会开事务。
- 内层 `IdempotencyCompletionFilter` 在工作单元之内、最贴近动作：动作正常返回后把结果写成快照并完成记录，事务型工作单元内与业务同一事务提交；业务回滚时完成记录一并回滚，完成写入失败则异常向外传播、工作单元不提交。

### 响应处理器
实现 `IIdempotencyResponseProcessor` 并注册到容器（可注册多个，按注册顺序执行），完成过滤器在保存快照之前对 `ObjectResult` 的非空结果值调用 `ProcessAsync` 就地处理；快照保存处理后的值，重播返回该快照。默认不注册任何处理器。处理器抛出异常时完成不写入，异常向外传播、工作单元不提交。

### 动作失败时的行为
- 事务型动作抛出异常：释放幂等键，调用方可用同一键重试。
- 非事务型动作抛出异常：标记为结果不确定，同一键之后返回 409，不会自动重试。
- 动作正常返回但结果无法保存为快照（流结果、超过 `MaxResponseBytes` 或不支持的结果类型）：同样标记为结果不确定。

### 限制
- 只适用于 MVC 控制器与动态 API，不适用于 Minimal API。
- 含文件或流参数的动作会被拒绝（415）。
- 快照只保存 `ObjectResult`（JSON 响应体）、空结果与仅状态码结果；响应体为空时按空保存，重播为 200 空响应或对应状态码。
- 已知限制：BasicApp 以 fork 框架源码（`XiHanFun*.slnx` 源码模式）构建时，fork 自带的幂等过滤器也会被注册，直到 fork 移除相应实现为止。

## 自动版本更新（UseAutoVersionUpdate）
已在 `XiHanBasicAppWebHostModule.OnApplicationInitialization` 中接入。

行为约定：
- **执行时机**：WebHost 模块是依赖图的根，其初始化晚于框架 `XiHanDataModule` 的数据库初始化，故脚本执行时表结构与基线数据均已就绪。
- **节点门控**：仅主节点执行（`XiHan:DistributedIds:SnowflakeId:WorkerId == 1`），多节点部署下不会重复跑脚本。
- **脚本来源**：`AppContext.BaseDirectory/UpdateScripts/*.sql`，文件名即版本号（如 `3.6.0.sql`），按语义化版本升序执行。
  脚本需在 WebHost 的 `UpdateScripts/` 目录下，并由 csproj 的 `CopyToOutputDirectory` 随输出/发布一同拷贝。
- **版本记录**：执行结果写入 `AppContext.BaseDirectory/version.txt`（格式 `版本^时间^是否已执行`）。
- **执行范围**：只执行「高于历史版本」且「不高于当前程序版本」的脚本；每个脚本在单个事务中执行，失败回滚且不记录为已执行。
- **全新部署**：无 `version.txt` 时视为最新版本，只落版本号、不跑任何历史脚本（不会在空库上误执行）。

## 使用方式
```csharp
[DependsOn(typeof(XiHanBasicAppWebCoreModule))]
public class MyWebModule : XiHanModule
{
    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var app = context.GetApplicationBuilder();
        // 执行本地版本升级脚本
        app.UseAutoVersionUpdate();
    }
}
```

## 目录结构
```text
XiHan.BasicApp.Web.Core/
  README.md
  XiHanBasicAppWebCoreModule.cs
  Extensions/
    AutoVersionUpdate.cs
  Idempotency/
    IdempotencyOptions.cs
    IdempotencyFilter.cs
    IdempotencyCompletionFilter.cs
    IdempotencyServiceCollectionExtensions.cs
    DefaultIdempotencyStore.cs
```
