# 数据模型与接口

## 1. 建模原则

- Markdown 是用户内容的可读载体；SQLite 保存事务、索引、映射与运行状态。
- ID 一旦分配不因文件移动或标题变化而改变。推荐 UUIDv7；实现前需确认所选库质量。
- 时间统一保存 RFC 3339 UTC，同时保存捕获时使用的 IANA 时区和本地日期。
- `null` 表示未知；空字符串不表示未知。来源字段缺失不得阻止记录。
- 所有外部对象有 `schema_version`；Capture API v1 拒绝未知请求字段，破坏性变更必须发布新版本。
- 派生对象总是引用输入 revision，不能只引用“当前文件”。

## 2. CaptureEntry

一条用户提交及其落盘结果。当前 Markdown 可以被用户修改；账本中的 `submitted_content` 保留提交时原貌，二者含义不同。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `schema_version` | integer | 初始为 1 |
| `entry_id` | UUIDv7 | 服务端稳定记录 ID |
| `request_id` | string/UUID | 客户端生成的幂等键，唯一 |
| `source_type` | enum | `desktop`、`weread`，未来可扩展 |
| `submitted_content` | string | 用户提交时正文，仅在本地账本保存 |
| `content_sha256` | string | 规范化前原始 UTF-8 内容哈希 |
| `received_at` | datetime | 服务端收到请求的 UTC 时间 |
| `created_at` | datetime | 记录捕获时间；默认等于服务端时间 |
| `occurred_at` | datetime? | 用户显式提供的事件发生时间，不决定默认落盘日期 |
| `timezone` | IANA zone | 计算 `local_date` 的时区 |
| `local_date` | date | 目标每日文件日期 |
| `source_context` | SourceContext? | 提交时冻结的来源快照 |
| `status` | enum | `received`、`writing`、`committed`、`recoverable`、`failed` |
| `file_path` | relative path? | `doc` 内相对路径，不接受客户端指定 |
| `file_revision_after` | string? | 成功后的内容哈希 |
| `committed_at` | datetime? | 回读验证完成时间 |
| `error_code` | string? | 稳定错误码，不含正文 |

约束：

- `request_id` 唯一。同一 `request_id` + 同 payload 返回原结果；同 ID + 不同哈希返回 `409 IDEMPOTENCY_CONFLICT`。
- `entry_id` 唯一。客户端可发送候选 `entry_id` 以支持离线队列，但最终由服务验证。
- 桌面和当前 P1 微信读书客户端的纯空白内容均拒绝。契约仍向后兼容可选 `selected_text`；若未来受支持的客户端仅提交引用，则该字段必须非空。
- 不对正文做破坏性 trim、HTML 清理或 Markdown 转义；只规范传输编码和换行策略，并记录原始哈希。

## 3. SourceContext

来源上下文是捕获时快照。`weread` v1 要求 `provider=weread` 和 HTTPS 微信读书 URL；书名、作者和章节允许缺失，缺失值不虚构。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `provider` | string | 如 `weread` |
| `work_id` | string? | 平台内书籍 ID |
| `work_title` | string? | 书名 |
| `authors` | string[] | 可为空，不压成单一字符串 |
| `chapter_id` | string? | 章节稳定 ID（若能获得） |
| `chapter_title` | string? | 章节展示名 |
| `url` | URL | 微信读书记录必填的提交时页面 URL；服务端只接受 HTTPS `weread.qq.com` |
| `selected_text` | string? | 用户主动选择的原文 |
| `selection_prefix/suffix` | string? | 短上下文，用于后续定位；受长度限制 |
| `captured_at` | datetime | 上下文冻结时间 |
| `extractor_version` | string | 页面适配器版本 |

作者观点、选文和用户评论必须在展示与模型输入中明确区分。无法识别书名时仍可保存，后续可人工补充或重新解析，但不得伪造元数据。

P1 v0.1.2 的 Chrome 插件不填充 `selected_text` 或前后文；这些可选字段仅为既有协议兼容和未来重新评估保留，不能据此推断当前插件支持网页选文。

## 4. SourceRevision 与 SemanticFragment

`SourceRevision` 表示某个 Markdown 文件/记录在一次扫描中的确定版本。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `source_revision_id` | UUID | revision ID |
| `source_key` | string | `entry:<entry_id>`；手工区域为 `manual:<relative_path>` |
| `entry_id` | string? | 捕获记录的稳定 ID；手工区域为空 |
| `file_path` | string | `doc` 内相对路径 |
| `content` | string | 该 revision 的正文副本，供本机索引与可追溯模型输入 |
| `content_sha256` | string | 当前内容哈希 |
| `source_type` | string | `desktop`、`weread` 或 `manual` |
| `created_at` | datetime? | 捕获时间；手工区域尽量由文件名推断 |
| `source_context_json` | string? | 从 P0 capture ledger 只读取得的来源快照 |
| `observed_at` | datetime | 扫描时间 |
| `predecessor_id` | UUID? | 上一 revision |
| `active` | boolean | 当前 revision 是否参与默认检索；删除/修改后旧 revision 保留但失效 |

`SemanticFragment` 是可重建派生对象，不是新的真相来源。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `fragment_id` | UUID | 片段 ID |
| `source_revision_id` | UUID | 精确来源 revision |
| `ordinal` | integer | revision 内顺序 |
| `text` | string | 片段文本或受控引用 |
| `start_offset/end_offset` | integer | .NET UTF-16 code unit offset，仅用于原文定位提示 |
| `topics/entities` | string[] | 模型候选，不等于用户确认事实 |
| `epistemic_status` | enum | `user_view`、`quoted_source`、`observation`、`question`、`hypothesis`、`mixed`、`unknown` |
| `action` | enum | `index_only`、`develop_paragraph`、`update_article`、`create_article`、`needs_review` |
| `extractor_version` | string | 切分/模型版本 |
| `analysis_version` | string? | 分析提示词版本；为空表示待分析 |
| `active` | boolean | 来源 revision 失效后同步失效 |

同一来源 revision + extractor 版本 + ordinal/text hash 构成幂等生成键。来源变化后旧片段标为 `superseded`，保留审计，不参与默认检索。

## 5. Article 与 ArticleRevision

`ArticleMetadata` 同时有 Markdown frontmatter 的最小公开部分和 SQLite 的完整运行部分。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `article_id` | UUID | 稳定 ID，不随标题/路径变化 |
| `title` | string | 用户可编辑标题 |
| `status` | enum | `seed`、`active`、`paused`、`archived` |
| `maintenance_mode` | enum | `manual_only`、`propose`、`auto_low_risk` |
| `relative_path` | string | `Writing/Articles` 下受控路径 |
| `current_hash` | string | 当前文章全文 SHA-256，是提案应用基线 |
| `revision_number` | integer | 每次 Agent 应用、外部编辑同步或回滚递增 |
| `updated_at` | datetime | 最近建立新基线的时间 |

`ArticleRevision` 保存 `content_sha256`、父 revision、actor（`human`/`agent:<run_id>`/`import`）、文件路径、创建时间和可选补丁引用。外部编辑无法精确知道操作者时，actor 标记 `external_unknown`，按人工编辑同等保护。

推荐 frontmatter 只放：

```yaml
---
pkws_schema: 1
article_id: 019...
status: active
maintenance_mode: propose
title: 文章标题
---
```

不把大型来源数组和运行状态塞进 frontmatter，以免影响阅读和人工编辑。

## 6. AgentRun 与 CodexWorkset

P2 v1 使用短时进程和 `agent.lock` 整进程独占租约，没有实现细粒度持久化 `WorkItem` 队列。`AgentRun` 保存：

- `run_id`、`run_type`（daily/weekly/monthly/topic）。
- `status`（running/succeeded/failed/interrupted）。
- `started_at`、`finished_at`。
- `items_processed`、`proposals_created`。
- `error_code`，不保存正文。

启动时遗留的 `running` 会转成 `interrupted`。fragment 分析和提案逐步提交，下一次 daily 工作集只领取 `analysis_version IS NULL` 的活跃片段；提案使用唯一幂等键阻止重复副作用。

`codex_worksets` 是 Codex 定期任务与本地工具的持久化边界：

| 字段 | 说明 |
| --- | --- |
| `workset_id/schema_version` | 稳定身份与协议版本 |
| `run_type/topic_name` | daily/weekly/monthly/topic 和可选显式主题 |
| `fragment_ids/source_revision_ids` | Codex 可引用的封闭 ID 集合 |
| `article_baselines` | 导出时 article ID 到全文 hash 的映射 |
| `status` | pending/completed；验证失败保留 pending 便于修正 |
| `submission_hash/proposal_ids` | 相同提交重试的幂等证据与产物 |
| `error_code` | 最近一次稳定失败类别 |

`CodexWorkset v1` 包含受限 fragment 正文、脱敏来源摘要、主题和文章目录。`CodexSubmission v1` 包含 fragment 分析、主题候选和最多受配置限制的变更请求。提交不能自由指定路径，source/fragment/article ID 必须来自工作集。契约见 `packages/contracts/codex-workset-v1.schema.json` 与 `codex-submission-v1.schema.json`。PKWS 不保存 API Key、模型提供方或调用费用，因为它不直接调用模型 API。

## 7. ChangeProposal

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `proposal_id` | UUID | 稳定 ID |
| `article_id` | UUID? | 文章提案的稳定目标 ID；回顾可为空 |
| `target_path` | string | 只能由程序在 `Writing/Articles` 或 `Reviews` 下生成 |
| `base_hash` | string? | 创建时为空，修订/覆盖回顾时为目标全文哈希 |
| `proposal_type` | enum | `create`、`revise`、`weekly_review`、`monthly_review` |
| `risk_level` | enum | low/medium/high |
| `proposed_content` | text | 已通过局部补丁计算出的完整候选结果；未批准时不写目标文件 |
| `rationale` | string | 修改原因 |
| `source_revision_ids` | UUID[] | 支持变更的活跃来源 revision，应用时重新验证 |
| `idempotency_key` | string | 类型、目标、基线、来源和候选内容的稳定哈希 |
| `status` | enum | pending/approved/rejected/applied/conflicted/failed |
| `created_at/reviewed_at/applied_at` | datetime? | 生命周期时间 |
| `result_hash/error_code` | string? | 应用回读结果或稳定失败类别 |

应用规则：CLI 的 `proposal approve` 是显式批准动作。应用前来源必须仍存在且 active，当前 hash 必须等于 `base_hash`；创建提案要求目标不存在。正文通过同目录临时文件、flush、原子移动和回读 hash 验证写入。冲突后保留用户文件，需重新生成提案。若进程在文件写入后、账本提交前中断，再次批准会通过结果 hash 恢复账本而不重复增加文章 revision。

## 8. KnowledgeCandidate

这是 P3 的交换候选，不是科学化工具正式对象。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `candidate_id` | UUID | PKWS 稳定 ID |
| `candidate_type` | enum | fact_claim/observation/explanation/question/hypothesis/experiment |
| `statement` | string | 候选陈述 |
| `epistemic_status` | enum | quoted/user_claim/inferred/hypothetical/unknown |
| `confidence` | number? | 模型置信提示，不等于真实性概率 |
| `source_refs` | object[] | source revision、span、文章 revision |
| `derivation` | object | 模型/规则/提示词版本和生成时间 |
| `dedupe_key` | string | 交换幂等键 |
| `review_status` | enum | proposed/exported/accepted/rejected/superseded |
| `external_ref` | string? | 科学化工具接收后 ID |
| `supersedes` | UUID? | 来源变化后的候选链 |

交换包须带 JSON Schema 版本、生成批次和内容哈希。对方接口未知，因此 P3 前先检查实际仓库；默认采用“导出待审核包 → 对方显式导入”的单向流程，双向更新另立协议。

## 9. Markdown 记录协议 v1（P0 已冻结）

目标是可读、边界稳定、元数据低干扰。示意：

```markdown
<!-- pkws:entry:v1 {\"v\":1,\"id\":\"019...\",\"created_at\":\"2026-10-21T06:35:12.0000000+00:00\",\"tz\":\"Asia/Shanghai\",\"source\":\"desktop\",\"content_sha256\":\"...\",\"content_bytes\":18} -->

### 14:00

这里是用户原样输入的 Markdown。

<!-- /pkws:entry:019... -->
```

标题只暴露捕获时间所在小时（`HH:00`），供用户浏览和后续 Agent 做粗粒度整理；精确 UTC 时间仍保存在隐藏元数据和账本中。桌面正文保持用户原样输入的 Markdown。微信读书记录由 Capture Host 组合为 `**来源：微信读书**` 和 `**我的笔记**` 两个可读区域；若兼容客户端确实提交了 `selected_text`，中间才增加 `> **原文**` 引用块。个人评论中的 Markdown 保持原样；完整结构化来源同时存入捕获账本。结束标记包含新生成的随机 ID，使正文意外撞到标记的概率极低。渲染器不解释用户正文中的指令。

P0 已用复杂 Markdown、Unicode、外部编辑、损坏/缺失结束标记与故障恢复测试冻结该语法。解析器只把同时存在匹配开始/结束标记的块视为完整记录；发现部分写入时停止自动重试并进入人工检查状态。

## 10. 本地 API v1（P0/P1 已实现）

### `POST /v1/captures`

请求：

```json
{
  \"schema_version\": 1,
  \"request_id\": \"019...\",
  \"entry_id\": \"019...\",
  \"source_type\": \"weread\",
  \"content\": \"我的想法\",
  \"occurred_at\": null,
  \"source_context\": {
    \"provider\": \"weread\",
    \"work_id\": \"123\",
    \"work_title\": \"示例书\",
    \"authors\": [\"作者\"],
    \"chapter_title\": \"第一章\",
    \"url\": \"https://weread.qq.com/...\",
    \"selected_text\": \"用户主动选中的原文\",
    \"captured_at\": \"2026-10-21T06:34:50Z\",
    \"extractor_version\": \"weread-dom-v1\"
  }
}
```

成功响应包含 `entry_id`、`status=committed`、`created_at`、`local_date`、`relative_path` 和 `duplicate`。不返回任意绝对路径。错误使用稳定代码和可重试标记，例如 `AUTH_FAILED`、`VALIDATION_FAILED`、`IDEMPOTENCY_CONFLICT`、`VAULT_UNAVAILABLE`、`WRITE_NOT_VERIFIED`。

### 其他最小端点

- `GET /v1/health`：进程与 API 可用性，不泄露 `doc` 绝对路径或令牌。
- `GET /v1/captures/{request_id}`：客户端超时后查询最终状态。
- `GET /v1/context`：返回展示用的日期/时区与服务能力，不返回敏感配置。
- `POST /v1/pairings`：以 6 位一次性码和调用方扩展 ID 换取独立凭证。只接受精确匹配的 `chrome-extension://<id>` Origin；配对码 5 分钟/一次使用，连续失败 5 次作废。

Capture API 只接受 `application/json`、有大小上限、Bearer token 和严格 Origin 策略。扩展凭证只能与签发时的扩展 Origin 一起使用；桌面主令牌保持向后兼容。客户端版本不受支持时返回明确升级错误。

机器可读契约位于 `packages/contracts/capture-v1.schema.json` 与 `packages/contracts/openapi.yaml`。API 固定监听 IPv4 loopback；`GET /v1/health` 与 `POST /v1/pairings` 不要求既有 token，后者仍必须通过一次性码和扩展 Origin 校验；其余端点需要 token。当前没有单独的服务端撤销端点，同一扩展重新配对会替换旧凭证。

