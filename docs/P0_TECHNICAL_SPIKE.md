# P0 Windows 技术 spike

日期：2026-10-21

## 结论

P0 采用 **.NET 10 LTS + WPF + ASP.NET Core + SQLite**。原计划的 Tauri 2/Rust 未进入业务实现，原因是目标机缺少 MSVC C++ Build Tools；尝试安装 Visual Studio 2022 Build Tools 时系统盘空间不足（安装器错误 `0x80070070`），无法在本机形成可重复的 Tauri 构建链。

这满足既有 ADR 的降级条件。选择 .NET 10 而非原文档中的 .NET 8，是因为本机开发日期为 2026-10-21，.NET 10 是有效 LTS，而 .NET 8 将在 2026-11-10 结束支持。

## 已验证

| 项目 | 结果 | 证据 |
| --- | --- | --- |
| SDK/编译 | 通过 | .NET SDK 10.0.402；Debug/Release 零警告零错误 |
| 单实例 | 已实现、自动化不可覆盖桌面交互 | named mutex + activation event |
| 右缘常驻入口 | 已实现、编译验证 | 6 像素透明 WPF 窗口；悬停展开按钮；无托盘 |
| 无键盘冲突唤起 | 已实现、待人工体验确认 | 鼠标贴边按钮；重复启动激活已有实例；不注册全局键盘组合 |
| 右侧笔记面板 | 已实现、编译验证 | 当前鼠标屏幕工作区 + DPI 转换；多屏/DPI 仍需人工验收 |
| 中文输入 | 使用原生 WPF TextBox | IME 行为仍需人工验收 |
| 本地 API | 通过自动化测试 | 仅 IPv4 loopback、认证、Origin、状态查询 |
| 可靠写入 | 通过自动化测试 | 追加、flush、回读、外部编辑保留、故障恢复、并发 |
| 便携发布 | 通过 | framework-dependent `win-x64` 产物 |
| 安装器/签名 | 未做 | P0 交付便携构建；SmartScreen 现实限制保留 |

## 决策影响

- P0 业务核心保持与 UI/API 框架隔离，P1 可以复用 HTTP 契约而无需了解 WPF。
- Windows 原生 UI 减少 WebView2/IME 变量，但产品仍为 Windows-only。
- P2 仍计划使用独立 Python worker，不把未来 Agent 逻辑塞入桌面进程。

参考：[.NET 支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy)、[WPF .NET 10 更新](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100)。
