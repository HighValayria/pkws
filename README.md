# Personal Knowledge Writing System (PKWS)

PKWS 是一个 Windows 本地优先的 Markdown 随手记与长期写作工具。程序平时收在屏幕右缘；鼠标贴近右缘后出现按钮，点击即展开笔记面板。P1 提供微信读书 Chrome Side Panel，把来源和个人 Markdown 笔记写入同一份每日文档；P2 Agent 从这些记录建立可重建索引，并以可审阅提案逐步创建或维护文章。它不注册全局快捷键、不使用托盘，也不修改系统代理、注册表或网络设置。

P0、P1 已实现并通过自动化验证，仍待真实环境人工验收。P2 本地核心与 Codex workset/submission 协议已通过临时文档库自动化验证；尚未创建真实 Codex 定期任务，也未完成长期提案质量与 Obsidian 人工验收。P3 未启动。

## 当前体验

- 屏幕右缘保留 6 像素感应条；鼠标进入后弹出打开按钮。
- 点击后从当前鼠标所在屏幕右侧打开连续的当日笔记，不再区分“已有内容”和“新增内容”两个区域。
- 每段上方只显示按小时取整的时间，例如 `14:00`；正文支持 Markdown 工具栏、`Ctrl+B`/`Ctrl+I` 和实时预览，保存后按 Markdown 排版显示。
- `Ctrl+Enter` 或右下角按钮保存，`Esc` 或右上角箭头收起；这些都是窗口内操作，不是全局快捷键。
- 草稿自动恢复；重复启动 `PKWS.exe` 会唤起已有实例。
- SQLite 幂等账本、单写入队列、append + flush + 按 ID 回读验证和启动恢复仍然生效。
- Chrome 插件使用 Side Panel；草稿按标签页保存在浏览器本地，来源在首次输入时冻结，保存失败或结果不确定时不会清空。网页选文引用已按用户决定从当前范围移除。
- 插件首次使用通过桌面端 6 位一次性码配对，不把 API 令牌写进扩展包或网页。
- Agent 对 `doc/Daily` 只读，使用独立 `index.db` 做 FTS5 检索；文章和回顾仍是普通 Markdown。
- 文章创建、局部修订和周期回顾默认先生成 `pending` 提案；只有用户批准后才原子写入并回读校验。

## 快速开始

要求 Windows 10/11 与 .NET 10 Desktop Runtime/SDK。

```powershell
Set-Location F:\Projects\notes
.\scripts\test.ps1
.\scripts\dev.ps1
```

发布便携构建：

```powershell
.\scripts\publish.ps1
.\artifacts\publish\win-x64\PKWS.exe
```

Agent 位于 `artifacts\publish\win-x64\agent\pkws-agent.exe`。PKWS 不调用模型 API；Codex 定期任务通过版本化 JSON 协议与本地工具协作：

```powershell
.\artifacts\publish\win-x64\agent\pkws-agent.exe scan
.\artifacts\publish\win-x64\agent\pkws-agent.exe search "关键词"
.\artifacts\publish\win-x64\agent\pkws-agent.exe status
.\artifacts\publish\win-x64\agent\pkws-agent.exe prepare daily --output .\artifacts\workset.json
# Codex 根据工作集生成 submission.json 后：
.\artifacts\publish\win-x64\agent\pkws-agent.exe submit .\artifacts\submission.json
```

普通启动只显示右缘入口；传入 `--show` 可直接展开面板，主要用于开发验收。

Chrome 插件的构建、开发者模式加载、配对和人工验收见 [P1 Chrome 插件说明](docs/P1_CHROME_EXTENSION.md)。
Agent 工作集协议、提案审批、回滚、Codex 定期任务设计和 P2 验收见 [P2 Agent 使用与验收](docs/P2_AGENT.md)。

## 数据位置与格式

- 用户文档：`<PKWS.exe 所在目录>\doc\Daily\YYYY\YYYY-MM-DD.md`，首次启动自动创建。
- 本机运行状态：`%LOCALAPPDATA%\PKWS`；开发时可用 `PKWS_DATA_DIR` 隔离。
- 桌面草稿：`draft.json`；幂等账本：`state\capture.db`；桌面 API token：`secrets\api-token`；扩展凭证哈希：`secrets\extension-clients.json`。
- Agent 控制账本：`agent\agent.db`；可重建全文索引：`agent\index.db`；工作集/提案限额：`agent\agent-config.json`。

每日文件是普通 Markdown。可见元信息只有整点标题；HTML 注释中保留稳定 ID、精确时间和校验哈希，供可靠重试与未来 Agent 使用，不显示在笔记界面中。设置面板只提供 doc 路径、打开文件夹和退出，不要求用户理解 Vault、时区或路径模板。

从旧版升级时，PKWS 不移动、不删除旧配置路径中的文件；新版只把后续记录写入同目录下新建的 `doc`。如需带入旧笔记，应先备份后人工复制，当前不做自动迁移。

自动测试只使用 `tests/fixtures` 和系统临时目录中的模拟文档库，不读取真实笔记。当前未签名、没有安装器、未创建 Codex 定期任务，也未初始化 Git。

## 文档

- [P0 手工验收](docs/P0_MANUAL_TESTS.md)
- [P1 Chrome 插件安装与验收](docs/P1_CHROME_EXTENSION.md)
- [P2 Agent 使用与验收](docs/P2_AGENT.md)
- [P0 技术 spike](docs/P0_TECHNICAL_SPIKE.md)
- [产品规格](docs/PRODUCT_SPEC.md)
- [系统架构](docs/ARCHITECTURE.md)
- [数据模型与 API](docs/DATA_MODEL.md)
- [架构决策](docs/DECISIONS.md)
- [项目状态](docs/PROJECT_STATUS.md)
- [后续路线](docs/ROADMAP.md)
