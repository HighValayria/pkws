# 系统架构

## 1. 架构结论

PKWS 采用本地模块化单体，而不是微服务。P0/P1 的唯一常驻核心是 Windows 采集主机；P2 的 Agent worker 按需或定时作为独立进程运行；Chrome 扩展、Obsidian 和未来科学化工具是边界外客户端。

```text
Desktop drawer ─┐
                ├─ versioned capture API ─> Capture Host ─> Daily Markdown
Chrome sidepanel┘                              │          └> capture ledger
                                              │
Obsidian <──── readable Markdown ──────────────┤
   │                                          │
   └─ user edits ─> file watcher/rescan ─> Indexer ─> SQLite FTS/index
                                                   │
                       Scheduler/user command ─> Agent worker
                                                   │
                              sources + articles ─> Change proposal
                                                   │
                                review/apply gate ─> Article Markdown
                                                   │
                              P3 export adapter ─> Knowledge candidates
```

进程之间通过版本化 API、SQLite 事务和文件协议协作。代码可以位于一个仓库，但不能绕过接口直接侵入其他模块的状态。

## 2. 进程与逻辑模块

### 2.1 必须独立的进程

| 进程 | 生命周期 | 职责 | 不负责 |
| --- | --- | --- | --- |
| Windows Capture Host | 用户启动后常驻、右缘入口管理 | 单实例、鼠标贴边入口、连续笔记面板、本地 API、ID/时间、幂等、安全追加、恢复 | 语义分析、文章写作 |
| Chrome Extension | 随浏览器页面 | Side Panel、微信读书上下文、Markdown 输入、提交与本地重试 | 直接访问 Vault、网页选文、最终时间/路径决策 |
| Agent Worker | 用户或 Codex 定期任务短时运行 | 增量索引、工作集、提交验证、提案、回顾、P3 候选 | 修改原始记录、监听键盘、调用模型 API、作为文件写入后门 |
| Obsidian | 用户控制 | 完整阅读、编辑、搜索与链接跳转 | PKWS 事务和幂等保证 |

未来科学化工具保持独立仓库和进程，只通过交换包或经验证 API 对接。

### 2.2 同一进程内的逻辑模块

Capture Host 内部：

- `shell`：6 像素右缘感应窗、展开按钮、单实例激活和当前屏幕右侧定位；不注册全局键盘组合、不使用托盘。
- `capture_ui`：连续当日记录、Markdown 草稿、小时级显示时间、提交状态和轻量设置。
- `local_api`：桌面主令牌、扩展 Origin 绑定凭证、一次性配对、Schema/大小校验和健康检查。
- `capture_application`：规范化请求、分配时间/ID、幂等状态机。
- `markdown_store`：路径解析、渲染、安全追加、刷新与回读验证。
- `capture_ledger`：请求账本、不可变原始提交、恢复状态、错误摘要。

Agent Worker 内部：

- `source_scanner`：检测 Markdown 新增、修改、删除和解析异常。
- `fragmenter`：确定性段落/长度切分和来源 UTF-16 offset 映射；主题、实体与认识论状态由 Codex 对有界工作集补充。
- `retrieval`：SQLite FTS5、结构化过滤、可选 embedding provider。
- `workset_protocol`：固定 fragment/source/article 基线，输出版本化 JSON，并校验 Codex 提交未越界。
- `proposal_engine`：生成、验证、存储和应用变更提案。
- `scheduler_boundary`：短时 CLI、整进程独占租约、workset/run 状态与幂等重跑；时间调度由 Codex 自动任务承担。
- `review_generator`：每日/每周/月度摘要与待确认事项。
- `knowledge_export`：P3 候选映射与导出，不直接写正式知识对象。

## 3. 分层职责

### L0 Source

`Daily/*.md` 是原始记录当前版本；捕获账本保留提交时原貌用于审计与恢复。Agent 对 Daily 只读。每日文件内使用低干扰 HTML 注释标记稳定记录边界，正文保持普通 Markdown。

### L1 Memory / Index

`agent.db` 保存来源 revision、记录/片段映射、主题分析、Codex 工作集和控制状态；独立 `index.db` 只保存 FTS5 派生索引。删除 `index.db` 后可由 `agent.db` 中的活跃片段重建，不会删除提案、文章 revision 或运行历史。若整个 `agent.db` 丢失，Daily 与文章 Markdown 仍可读，但审批历史和工作集账本不能完整重建，必须依靠备份。

### L2 Writing

`Writing/Articles/*.md` 保存文章正文与少量稳定 frontmatter。文章元数据、来源映射和 revision 在 SQLite 中有镜像，但 Markdown 仍可独立阅读。Agent 先把完整候选结果与基线哈希写入 `ChangeProposal`，用户通过 CLI 查看 diff 并批准后才原子替换文章文件。

### L3 Views

P0 的 PKWS 面板提供轻量连续阅读和追加；外部 Markdown 编辑器可直接打开文件。经批准的回顾写入 `doc/Reviews/Weekly|Monthly`；完整提案、运行和模型调用账本留在本机应用数据，不污染用户文档树。

### Control

规则、协议版本、任务状态、变更审计、预算、重试与质量门集中在 Control 模块。Codex 自动任务只负责定时启动一次项目任务和完成受限推理；业务状态、恢复和幂等仍由 `agent.db` 承载。

## 4. 存储布局

### 4.1 用户文档（长期、可读、可备份）

根目录固定为 `<程序目录>/doc`，便携移动和备份时应连同程序目录整体复制：

```text
<程序目录>/doc/
  Daily/                  # 原始每日记录，Agent 只读
    YYYY-MM-DD.md
  Writing/Articles/       # 用户与 Agent 共同维护的文章
  Reviews/Weekly/         # 经批准的周回顾
  Reviews/Monthly/        # 经批准的月回顾
```

不要求用户使用复杂文件夹和标签。`Daily` 与 `Writing` 是数据安全边界，不是知识分类体系。

### 4.2 本机应用数据（机器状态、不进 doc）

```text
%LOCALAPPDATA%/PKWS/
  config.json             # 固定文档根、时区、端口策略；不含令牌
  draft.json              # 未提交草稿、开始日期与稳定 request_id
  window.json             # 窗口宽高
  secrets/
    api-token             # 桌面主令牌，不提供给扩展
    extension-clients.json # 扩展 ID 与凭证哈希，不保存扩展明文令牌
  state/
    capture.db            # 幂等账本、原始提交与恢复状态
  agent/
    agent.db              # 来源 revision、片段、主题、proposal、文章版本和 run 审计
    index.db              # 可删除重建的 FTS5 派生索引
    agent-config.json     # 工作集与提案限额，不含密钥/模型设置
    agent.lock            # 短时 worker 独占锁
  logs/                   # 轮转、默认不含正文
  recovery/               # 未完成写入和人工恢复报告
```

P2 已把 FTS5 派生索引拆为 `index.db`，控制与 revision 表保留在 `agent.db`。捕获账本仍由 P0 单独维护，Agent 只读其中的来源上下文。

### 4.3 项目源代码（P0–P2 实际结构）

```text
apps/
  desktop/Pkws.Desktop/   # .NET 10 WPF shell/UI
  browser-extension/      # P1 Manifest V3 TypeScript 源码、fixture、测试和 dist
  agent/Pkws.Agent.Cli/   # P2 .NET 10 短时 CLI
src/
  Pkws.Core/              # 捕获领域、SQLite 账本、Markdown store
  Pkws.Api/               # ASP.NET Core loopback API
  Pkws.Agent/             # P2 索引、Codex 工作集协议和文章维护核心
packages/
  contracts/              # P0 JSON Schema/OpenAPI
tests/
  Pkws.Tests/             # xUnit v3，临时模拟 Vault
docs/
```

## 5. 核心数据流

### 5.1 可靠捕获

1. 客户端生成 `request_id`，提交内容和可选来源上下文。
2. API 验证令牌、Origin、大小、Schema 和版本。
3. Capture Host 在事务中按 `request_id` 查账本：已提交则返回既有结果；同 ID 不同 payload 返回 409。
4. 新请求写入账本为 `received`，保存原始 payload 哈希与必要恢复数据。
5. 主机生成/确认 `entry_id`，以服务端 UTC 时间和配置时区计算目标日期。
6. 对目标 Vault/日期加进程内队列和文件级互斥，重新读取目标文件状态。
7. 渲染自包含 Markdown 块，追加、flush，并按 `entry_id` 回读验证。
8. 账本标记 `committed`，保存文件相对路径、前后指纹和字节范围提示，返回结果。
9. 若响应丢失，客户端以同 `request_id` 重试，第 3 步返回同一 `entry_id`。

SQLite 与 Markdown 无法组成真正的跨资源原子事务，因此恢复流程是设计的一部分：启动时扫描非终态账本；若目标文件已经含 `entry_id` 则补记提交，否则在验证目标文件未出现该 ID 后重试。不得仅凭字节偏移判断，因为用户可能编辑文件。

### 5.2 Chrome 扩展配对与捕获

1. 用户在桌面设置中主动生成 6 位一次性码；码只在内存中存在，5 分钟后或 5 次失败后失效。
2. 扩展 Service Worker 从自身 `chrome.runtime.id` 构造配对请求。服务同时验证 `chrome-extension://<id>` Origin 与请求 ID 一致。
3. 服务签发独立 256-bit 扩展凭证；明文只返回一次并保存在 `chrome.storage.local`，主机持久化扩展 ID 与 SHA-256。相同扩展再次配对会轮换旧凭证。
4. Content Script 只发送受限的 `PageState/SourceContext` 给扩展后台，不接触凭证、不请求 localhost，也不能指定目标 URL 或文件路径。
5. Side Panel 的非空草稿冻结一份来源快照。当前页变化只触发告警；保存仍可使用冻结快照，显式改绑会生成新的请求 ID。
6. Service Worker 从固定 API 基址构造请求。POST 超时后先查询同一请求 ID；无法确认时保留草稿供同 ID 重试。
7. Capture Host 验证 `weread` provider、HTTPS 微信读书 URL、字段和引用大小，再由服务端组合可读 Markdown 并进入统一账本/追加链路。

### 5.3 外部编辑与增量索引

1. P2 v1 使用手动或 Codex 定期任务周期扫描；未引入常驻文件监视器。
2. 解析稳定 Entry ID，并以正文哈希、相对路径、来源类型和来源上下文确定 revision。
3. 解析稳定记录边界；对每个记录计算正文哈希。无法解析的区域保留为手工内容并产生诊断。
4. 新/变更记录进入 fragment work item；删除记录只撤销派生映射，不删除审计历史。
5. fragmenter 输出带 UTF-16 字符范围和原文 revision 的片段；FTS 写入与 revision 更新使用同一 SQLite 操作，启动时用数量一致性检查修复派生索引。
6. 用户再次编辑只使相关记录/片段失效，不全量重算历史。

### 5.4 文章提案与应用

1. `prepare` 按日/周/月/主题选择来源片段，固定 fragment、source revision 和文章 hash 基线，导出 `CodexWorkset v1`。
2. Codex 定期任务把工作集视为不可信数据，输出 `CodexSubmission v1`；它不直接指定目标路径或写入用户文件。
3. `submit` 校验 ID 必须来自工作集、文章 hash 未变、变更类型与周期匹配、预算未超限，再产生局部 `find/replace` 或新文章/回顾提案。
4. proposal engine 二次校验引用存在、目标范围、禁改区域和补丁可应用性。
5. 保存 `ChangeProposal(base_hash, proposed_content, rationale, source_revision_ids)`；CLI 可显示与当前文件的统一 diff。
6. 应用时重新计算文章哈希。相同则原子写入临时文件、fsync、替换并记录审计；不同则标记冲突。
7. 索引器把新文章 revision 作为后续基线；重复 run 通过 proposal 幂等键避免重复修改。

## 6. 技术选型

### P0 已采用方案

- **.NET 10 WPF**：Windows 单实例、透明右缘感应窗、原生输入与右侧笔记面板。
- **ASP.NET Core minimal API**：同进程内只绑定 `127.0.0.1` 的 Capture API。
- **Microsoft.Data.Sqlite**：捕获账本使用 SQLite WAL、`synchronous=FULL` 与事务。
- **SQLite + WAL + FTS5**：适合单机事务、全文检索和可检查状态；初期无需 PostgreSQL、消息队列、向量数据库。中文检索必须在 P2 比较 `unicode61` 与 `trigram` 等 tokenizer，不能默认英文分词效果可直接迁移。
- **.NET 10 console worker**：P2 复用已有 C# Markdown/哈希规则，减少部署运行时和跨语言契约；仅提供确定性的 workset/submission 协议，不内置模型 HTTP 客户端。
- **Codex 定期自动任务**：承担时间调度和语义推理；本地文件任务要求电脑开机且 Codex 桌面应用可用，业务恢复不依赖会话记忆。

### 备选与切换条件

| 方案 | 优点 | 主要代价 | 切换条件 |
| --- | --- | --- | --- |
| .NET 10 WPF + ASP.NET Core | Windows 集成与部署成熟、同进程本地 API | UI 仅 Windows、C# 与 Python/TS 三栈 | **P0 已采用** |
| Python FastAPI + PySide6 | 与 P2 同语言、原型快 | 打包体积、事件循环、全局快捷键和签名维护较复杂 | 团队明确优先 Python 单栈且接受部署成本 |
| Chrome Native Messaging | 不暴露本地端口、安全边界强 | 安装注册复杂、桌面 UI 仍需宿主 | 企业安全策略禁止 localhost API |
| Obsidian 插件 | 编辑体验天然一致 | 快捷抽屉、浏览器接入和常驻依赖 Obsidian | 用户接受必须运行 Obsidian，且 P0 spike 证明足够 |
| AutoHotkey | 验证快 | 难承载可靠事务、恢复和可维护 UI | 只用于一次性 spike 或启动桥接 |

## 7. 安全与信任边界

- 本地 API 监听可配置回环端口（默认 27123），不监听 LAN；端口冲突不影响桌面内部直连领域服务。
- 首次运行生成 256-bit 桌面主令牌，放在用户 LocalAppData 下并设隐藏属性；扩展不读取该令牌。扩展凭证由一次性码签发并按扩展 ID 轮换。
- 扩展请求同时校验 Bearer token 与已配对的精确 `chrome-extension://<id>` Origin；普通网页 Origin、无凭证、凭证与扩展 ID 不匹配均拒绝。CORS 只负责浏览器互操作，不承担认证。
- API 设置请求体上限、严格 JSON 字段、来源 URL/字段长度和配对尝试上限，不接受客户端文件路径。通用捕获速率限制与单独的服务端撤销 UI 仍是已披露的后续加固项。
- 扩展 host permissions 仅为 `weread.qq.com` 与 `127.0.0.1:27123`；不申请 Cookie、文件系统、代理、全站或快捷键权限。
- 浏览器内容、引用和文章来源全部作为数据处理；模型输出也作为不可信候选，经过 Schema 和策略校验。
- 路径由程序目录下固定 `doc` 根与受控相对规则拼接，规范化后必须仍在根目录内，防止路径穿越。
- 日志结构化并脱敏。诊断包默认不包含正文，用户明确选择后才导出最小必要样本。

## 8. 一致性、备份与恢复

- Capture Host 是 Daily 的自动追加单一写入者；Agent 永不写 `Daily`。无法阻止 Obsidian 外部全文件保存，因此扫描会为内容变化建立 successor revision，不回滚用户内容。
- 原始提交账本和 `agent.db` 需要备份；`index.db` 可完全重建。控制账本不能仅从 Markdown 完整恢复，不应与派生索引一起删除。
- 文章采用原子替换、revision 校验和补丁审计。Windows 上替换/杀毒软件行为在 P0/P2 分别做故障注入测试。
- 可选 Git 只覆盖 Vault Markdown 与人类可见提案，不提交数据库、日志、令牌或外部缓存。Git 不能替代运行时冲突检测。

## 9. 可观测性

最小指标：捕获成功/失败/重复数、落盘延迟、非终态账本、文件解析异常、索引滞后、任务成功/重试/隔离数、提案应用/冲突/拒绝数、模型调用次数/延迟/估算费用。所有指标以 ID 和计数为主，正文不进入普通日志。

