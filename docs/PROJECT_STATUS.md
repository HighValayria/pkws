# 项目状态

更新日期：2026-10-22

## 当前阶段

P0、P1 的阶段门为 **实现完成，待用户手工验收**。P2 的本地核心与协议已实现并通过自动化验证；Codex 项目级定期任务已按北京时间每天 09:00 启用，并在周一和每月 1 日顺序追加周/月处理，但首次真实运行、长期内容质量与 Obsidian 人工验收尚未完成，因此 P2 阶段门尚未关闭。PKWS 不内置模型 API 调用。P3 未启动。

## 本轮完成

- 删除托盘依赖和所有全局快捷键能力；未新增键盘钩子、注册表或网络/代理设置操作。
- 新增独立右缘入口：收起时为完全透明的 6 像素感应窗，鼠标进入后显示按钮，点击展开当前屏幕右侧面板；感应窗会跟随鼠标所在显示器。
- 笔记界面改为连续当日页面：历史记录与当前 Markdown 输入同版式排列，不再显示“已有内容（只读）/新增内容”产品分区。
- 增加轻量 Markdown 编辑体验：标题、粗体、斜体、列表、引用、代码按钮，窗口内 `Ctrl+B`/`Ctrl+I`，实时预览，以及已保存记录的 Markdown 排版；预览不执行内容。
- 修复连续页面的嵌套滚动冲突：历史 Markdown、输入框和预览区上的鼠标滚轮统一驱动外层笔记页面，避免滚动一小段后被内层控件吞掉。
- 右上角设置与收起入口改为圆角、无边框的矢量图标按钮，并提供悬停和按下反馈。
- 每段可见元信息只显示 `HH:00`；隐藏协议继续保存精确 UTC 时间、稳定 ID 和哈希。
- 用户内容根固定为 `<PKWS.exe 所在目录>/doc`，首次启动自动创建；设置保留路径、打开文件夹、P1 插件配对和退出。
- 保留单实例激活、草稿恢复、跨日提示、SQLite 幂等账本、串行追加、flush、按 ID 回读验证和崩溃恢复。
- 新增 Markdown 记录解析/原文还原测试；旧 `hotkey` 和 `vaultPath` 配置字段在下一次保存时被移除。

### P1 微信读书输入端

- 新增可开发者模式加载的 Manifest V3 TypeScript 扩展，使用 Chrome Side Panel、Content Script 和 Service Worker；v0.1.2 权限仅覆盖 Side Panel、草稿存储、活动标签页、微信读书域名和固定 loopback API。
- 新增 `weread-dom-v1` 页面适配器：从阅读页 URL 与集中选择器提取书籍/章节快照，以 MutationObserver、导航事件和节流 URL 检查响应 SPA 变化；两种脱敏 DOM fixture 覆盖主选择器与降级选择器。
- 侧栏支持 Markdown 自由输入、按标签页本地草稿、明确保存状态和连接设置。
- “当前来源”卡片支持收起为单行书名，并在浏览器本地记住折叠状态，为笔记输入区释放纵向空间。
- 真实微信读书页面上选文读取未达到可靠性门槛；用户决定跳过后，v0.1.2 已移除引用按钮、右键菜单、选区缓存/读取代码以及 `contextMenus`、`scripting` 权限，不再将该能力列为 P1 验收项。
- 草稿第一次出现内容时冻结来源；切书、切章或切标签页不会静默覆盖，用户可按旧来源保存或显式改绑，改绑同时更换 `request_id`。
- Service Worker 独占本地 API 凭证和固定请求地址；超时后以相同 `request_id` 查询状态，未知结果、离线和错误均保留草稿。
- P0 增加一次性 6 位配对码（5 分钟/5 次失败上限）和按 Chrome 扩展 ID/Origin 绑定的独立凭证；桌面端只保存凭证 SHA-256，重复配对轮换旧凭证。
- 微信读书记录由单一写入者渲染成人类可读的“来源 / 我的笔记”Markdown，同时在账本保留结构化 `SourceContext`；既有可选引用字段只作协议兼容保留。
- 更新安装、配对、更新、权限说明与 15 项真实 Chrome/微信读书人工验收清单：`docs/P1_CHROME_EXTENSION.md`。

### P2 Codex 整理与文章维护

- 新增 .NET 10 无 UI Agent 核心和短时 CLI，对 `doc/Daily` 只读；控制状态在 `agent.db`，FTS5 派生索引在可删除重建的 `index.db`。
- 实现增量 revision/fragment、删除与外部编辑识别、中文短词回退检索、微信读书来源上下文脱敏摘要和诊断记录。
- 用 `CodexWorkset v1` / `CodexSubmission v1` 替换了内置模型 HTTP 客户端；已移除 API Key/提供方配置和 Windows Task Scheduler 脚本。
- `prepare` 导出受限 fragment/source/article 基线；`submit` 校验封闭 ID 集合、周期、预算、文章 hash 和精确补丁，相同提交可幂等重试。
- 实现主题、文章创建/局部修订、周/月回顾、pending 提案、diff、approve/reject、基线冲突保护、锁定区、崩溃窗口恢复和文章回滚。
- 新增契约 `packages/contracts/codex-workset-v1.schema.json` 和 `codex-submission-v1.schema.json`，并同步更新产品、架构、数据模型、路线、决策与使用文档。
- 已创建并启用 Codex 项目级定期任务“PKWS 定期整理”：每天 09:00 处理前一天，周一追加上周，每月 1 日追加上月；周期重合时串行执行。任务仅生成待审提案，不自动批准，尚待首次真实运行验证。

## 验证证据

```text
dotnet build PKWS.slnx --no-restore --disable-build-servers -m:1
结果：0 warnings, 0 errors

tests/Pkws.Tests/bin/Debug/net10.0/Pkws.Tests.exe --output Detailed --no-ansi
结果：53 total, 53 passed, 0 failed, 0 skipped；其中 P2 Agent 26/26

npm run check（apps/browser-extension）
结果：TypeScript 通过，无错误

npm test（apps/browser-extension）
结果：3 files, 7 tests passed

npm run build（apps/browser-extension）
结果：成功生成 dist/ 可加载目录

scripts/publish.ps1 -OutputDirectory artifacts/verify/publish-codex-p2-final
结果：Desktop 和 Agent Release win-x64 都发布成功；未覆盖用户当前的正式发布目录。

dotnet build apps/desktop/Pkws.Desktop/Pkws.Desktop.csproj -c Release --no-restore ... -p:UseAppHost=false -o artifacts/verify/scroll-fix
结果：滚动修复独立输出编译通过，0 warnings, 0 errors

pkws-agent prepare/submit/proposal approve（tests/fixtures/p2-vault 复制到 artifacts/verify 后运行）
结果：3 来源/3 fragments 生成 1 个 pending 文章提案；批准前 0 文件，批准后 1 文件，原子写入与回读验证成功。
```

自动测试全部使用系统临时目录中的模拟文档库，未读取或写入真实用户笔记。

## 待人工验收

- 右缘感应区是否容易触发、是否误触；按钮收回延迟和面板宽度是否合适。
- 中文 IME、鼠标贴边、窗口前台激活、第二实例、睡眠/唤醒。
- 多显示器，以及 100%/125%/150% DPI、不同任务栏位置和全屏应用。
- 连续页面视觉、长 Markdown、跨历史/输入/预览区域的连续滚动、复制和草稿恢复。
- Markdown 工具栏、预览排版以及右上角图标的可识别性和操作手感。
- 发布目录 `doc` 的实际写入、外部编辑并发和便携目录移动/备份。
- Chrome 开发者模式加载、扩展图标打开 Side Panel、左右侧栏设置和浏览器重启。
- “当前来源”收起后的单行截断、折叠状态恢复和窄侧栏显示。
- 登录后的真实微信读书页面：书名/作者/章节/ID、切书/切章/标签页和 DOM 延迟加载。
- 桌面配对、服务停止/恢复、超时未知结果、快速双击与 Obsidian 中来源/笔记排版。
- 观察已启用的 Codex 定期任务至少一个完整日/周周期，核对静默无变化、失败通知和 pending 提案。
- 使用非敏感实际笔记评估主题命名、引用忠实度、文章重复率和长期 churn；在 Obsidian 人工修改后验证冲突说明。

详见 `docs/P0_MANUAL_TESTS.md` 与 `docs/P1_CHROME_EXTENSION.md`。这些项目未完成前不宣称对应阶段门通过。

## 已知限制与风险

- 右缘入口是顶层透明 WPF 窗口，必须运行应用才存在；多屏/DPI 和全屏遮挡尚未实机验收。
- `doc` 位于程序目录，因此当前发布目录必须可写；不适合直接放在受保护的 `Program Files`。未来安装器需要另行决定可写布局与迁移方案。
- 当前为未签名、无安装器的 framework-dependent 便携构建，目标机需要 .NET 10 Desktop Runtime。
- 未实现开机启动、自动备份、Git 快照、历史块原地编辑或图形化恢复报告。
- P1 配对凭证已实现 Origin/扩展 ID 绑定、只存哈希和一次性码尝试上限；尚未提供单独的服务端撤销列表 UI，当前通过同扩展重新配对完成轮换。
- 真实微信读书 DOM 尚未人工验证；选择器失效时会降级，但不能据 fixture 测试声称页面已兼容。
- 扩展草稿按 Chrome 标签页 ID 保存，并按来源保留最近恢复副本；浏览器完全重启、多个同来源草稿和标签页恢复行为仍需实测。
- 网页选文引用已从 v0.1.2 和 P1 验收范围移除；协议中的可选选文字段仅为向后兼容保留，当前插件不会填充。
- Codex 本地定期任务依赖电脑开机且桌面应用可用；错过时刻的实际补跑行为需在创建自动任务后人工观察。
- P2 当前没有细粒度 work-item 租约/隔离队列 UI；采用整进程锁、pending workset、稳定错误码和手动放弃后重建恢复。

## 下一步（需要用户验收或明确授权）

1. 按 `docs/P0_MANUAL_TESTS.md` 完成桌面端人工验收。
2. 按 `docs/P1_CHROME_EXTENSION.md` 加载 `apps/browser-extension/dist`，完成真实 Chrome/微信读书闭环验收并记录结果。
3. 观察已启用的 Codex 项目级定期任务首次运行，并完成 P2 提案质量、审批与 Obsidian 冲突人工验收。
4. 用户明确授权前不启动 P3，不初始化 Git，不做开机启动或系统级安装。
