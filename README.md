# VantreLingo

面向 Windows 的轻量 LLM 语言小工具：**翻译、截图取字、本地术语、客户信息与缺项整理**。

> 已接通四项功能的 v0.1 实现；Windows 11 完整实机验收尚未完成，实施范围见 [Issue #1](https://github.com/hx123321/VantreLingo/issues/1)。

## 当前可运行基座

- 独立 WPF 程序 `VantreLingo.exe`，单实例、托盘、复用工具窗口、设置窗口。
- 手动输入翻译；`Alt+D` 主动读取明确选区且始终只读；`Ctrl+Alt+G` 按保存的审查/快速模式处理。
- 写作审查支持可编辑译文、文本 Diff、替换/追加/复制/取消；写回前重新验证原控件、选区和上下文。
- 快速模式只尝试标准 Unicode Win32 `Edit` 控件的一次性选区写回，不弹窗、不激活目标、不操作剪贴板；无法验证或存在风险时使用托盘提示，结果留在内存供主动查看。
- 一个 OpenAI-compatible 适配器，非流式完整 JSON、超时、取消、迟到结果隔离，不自动切换 Provider。
- 智能语言路由、可搜索语言选择、最近有效外语记忆，以及基础数字/URL/邮箱/模板变量风险提示。
- 版本化 JSON、原子文件替换、DPAPI CurrentUser 密钥存储；没有正文日志或历史库。

- 可组合风格编辑、导入/导出、本地冲突校验；正式文件锁定严格忠实，自定义提示需成功测试并明确授权才能快速应用，修改或导入后授权失效。
- 本地 JSON 术语 CRUD、启停、草稿/激活、双向匹配、最长短语优先、上下文/词边界、冲突检查、允许及禁用变体。
- 客户信息、分产品需求、缺项/歧义/冲突、建议追问四区；原文证据及产品独立范围校验，人工修改、复制 Markdown/JSON 和显式另存。
- `Alt+S` 主动框选、本地 Windows OCR、识别文本编辑后主动翻译/整理。**OCR 需要 MSIX 身份和系统已安装的语言能力**；普通 EXE 支持其余功能，不自动上传截图或下载模型。
- 多 Provider 管理、固定/智能目标语言、模式及快捷键设置、OCR 已安装语言选择；普通设置导出不包含密钥或密文。

浏览器、VS Code、Word、RichEdit 及其他未验证控件不提供自动写回；能读取明确选区时支持审查/复制，否则使用手动粘贴。没有模拟复制或粘贴路径，不发送 `Ctrl+A` 或 Enter。保护规则不能证明完整语义等价。

原生写回仍需 Windows 实机验收。Win32 没有跨进程的原子文本条件替换；本实现发送前两次验证完整上下文和范围、发送后核对完整文本，但不能仅凭 Core 检查证明并发编辑绝无竞争窗口。写入超时或状态不确定时消费快照、不重试、不自动恢复，也不更新语言记忆。默认审查，用户需在设置里主动切换快速模式；尚未将任何应用标为已通过 A/B 级兼容。测试步骤和支持边界见 [M1 验证记录](docs/M1-VALIDATION.md)。

## 构建和验证

开发需要 .NET 10 SDK。Windows 11 x64 可以直接运行 WPF；Linux 只能交叉编译 Windows 产物和执行跨平台 Core 检查。

```powershell
# Windows：锁定依赖恢复、Release 构建、Core 检查
./scripts/Build.ps1 Verify
# 输出用于运行的 Windows 文件
./scripts/Build.ps1 Publish
```

```bash
# Linux / Bash：优先使用 .tmp/dotnet 内的 SDK，否则使用 PATH 中的 dotnet
./scripts/build.sh verify
./scripts/build.sh publish
```

运行 `artifacts/win-x64/VantreLingo.exe`，首次在“设置”中填写 Provider API 基础地址、模型和密钥。发布包依赖 **.NET 10 Desktop Runtime x64**，不需要 SDK；下载地址：[Microsoft .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)。OCR 安装与打包步骤见 [MSIX / OCR](docs/PACKAGING.md)。Provider 须支持 Chat Completions 的 `response_format: json_object`，不支持时明确失败，不静默降级。当前环境没有真实 Provider 凭据，HTTP 检查使用内存模拟响应。

## 文件约定

```text
src/VantreLingo.Desktop/       Windows 宿主及唯一选区捕获入口
src/VantreLingo.Core/          配置、路由、请求隔离、LLM 协议
tests/VantreLingo.Core.Checks/ 跨平台规则回归检查；不进入发布包
tests/VantreLingo.Windows.Checks/ Windows 原生互操作检查；不进入发布包
packaging/                    MSIX 清单和标识图标
scripts/                      验证、发布与固定上游检查脚本
licenses/                     上游与实际保留依赖的许可证
artifacts/win-x64/             可运行结果；不提交 Git
.tmp/                         SDK、上游检查副本、NuGet、bin/obj、临时检查数据
```

只保留必要源码、构建入口、锁文件和既有产品文档，不将完整上游放进正式源码树。`.tmp` 可整体删除后重建；上游检查副本只允许放在 `.tmp`。普通 `dotnet build` 也会将中间产物和 NuGet 包放入 `.tmp`，推荐使用脚本，让 CLI/HTTP 缓存及临时目录同样集中在此处。

运行时设置只写到 `%LocalAppData%/VantreLingo/settings.json` 、`providers.json`、`presets.json` 与 `glossary.json`，写入临时文件在该目录的 `.tmp`。Provider 元数据示例见 [providers.example.json](examples/providers.example.json)。损坏或未知版本的配置不会被默认值覆盖；API key 密文绑定原 Windows 用户，不应当作可迁移的明文配置。默认不保存原文、译文、选区或客户正文。

## 本次验证边界

2026-10-01 在 Debian 13 x64、.NET SDK 10.0.401 / Runtime 10.0.12 上执行锁定恢复、Release 交叉编译及 62 项 Core 检查。发布目标为 Windows x64，未在 Linux 执行 WPF。托盘、全局热键、真实 UI Automation 控件兼容性、原生写回和 DPAPI 仍需 Windows 11 实机验证；没有将其记录为验收通过。Windows CI 会构建、执行隔离原生控件/DPAPI 检查并验证未签名 MSIX；它不代替 Windows 11 用户环境的完整验收。实现和 P0 证据映射见 [交付验证记录](docs/V0.1-VALIDATION.md)。

## v0.1 只做四件事

1. **LLM 多语言翻译 / 写回**
   - 阅读翻译、写作翻译。
   - 审查模式与快速覆盖模式。
   - 默认“智能 ↔ 中文/最近外语”。
   - 记住最近一次有效外语。
   - 多语言手动切换。
   - 可组合的翻译场景、专业方向、语气、性格、篇幅和忠实度。

2. **本地术语库**
   - UTF-8 JSON 本地维护。
   - 按语言、方向、上下文应用。
   - 不接 ERP、独立站或云端词库。

3. **客户信息与询盘整理**
   - LLM 从用户明确提供的文本/OCR 文本提取客户信息与产品需求。
   - 同时整理缺项、歧义、冲突和建议追问。
   - 不自动补事实、不自动发送、不自动建业务单。

4. **截图取字**
   - 用户主动截图。
   - 本地 OCR。
   - 识别文本可修改，然后进入翻译或客户整理。

## 默认快捷入口

| 操作 | 默认快捷键 | 行为 |
|---|---|---|
| 阅读翻译 | `Alt+D` | 只读浮窗，不修改来源 |
| 写作翻译 | `Ctrl+Alt+G` | 审查或快速模式 |
| 截图取字 | `Alt+S` | 主动框选 → OCR |

## 第一版明确不做

图片优化、整份 PDF 翻译、报价/装箱/汇率计算、SEO 工具、语音、知识库、聊天 Agent、插件市场、云同步、自动发送客户消息、自动建业务单，以及 VANTRE 独立站/ERP/CRM API 集成。

LLM Provider 连接是翻译与客户整理本身所需能力，不属于“业务 API 集成”。

## 文档

- [完整产品需求](docs/PRD.zh-CN.md)
- [技术架构](docs/ARCHITECTURE.md)
- [验收要求](docs/ACCEPTANCE.md)
- [固定上游与许可证](docs/UPSTREAM.md)

## 固定基座

基于 **STranslate v2.0.10** 做裁剪式派生：

- Upstream: `STranslate/STranslate`
- Tag: `v2.0.10`
- Commit: `2a75118fe0fabc1135a619f4393eb3a8d936c5cd`
- 技术栈：C# / .NET 10 / WPF / Windows
- 上游主仓库许可证：MIT

不引入 ZyntaxAI 源码，不建立第二套选区读取和写回机制。STranslate 只作为固定底座，后续必须按 Issue #1 对运行模块、依赖、菜单、后台监听和打包内容做实质裁剪。
