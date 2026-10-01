# 固定上游与派生约束

## STranslate

VantreLingo v0.1 固定使用：

- Repository: `STranslate/STranslate`
- Tag: `v2.0.10`
- Commit: `2a75118fe0fabc1135a619f4393eb3a8d936c5cd`
- Target: .NET 10 / WPF / Windows
- Upstream license: MIT

固定 commit 的目的：

1. 避免上游持续变化导致需求和实现基线漂移；
2. 让删除模块、依赖和回归测试可复现；
3. 后续若升级上游，必须作为单独变更评估。

## 为什么不用 ZyntaxAI 作为第二基座

本项目不直接复制 ZyntaxAI 源码。

原因不是否定其交互，而是：

- STranslate 已经具备选区、热键、窗口、翻译和替换基础；
- 两个基座会形成两套捕获/写回模型；
- 本项目需要的 Diff/审查/安全覆盖可在同一宿主内实现；
- 避免混入不同许可证与架构负担。

可以参考交互思想，但实现保持单一代码路径。

## 许可证

上游 STranslate 主仓库使用 MIT License。

导入任何上游源码时：

- 保留原 MIT copyright notice；
- 保留许可证文本；
- 不删除必要 attribution。

注意：

> 上游主项目 MIT 不代表所有第三方 NuGet、二进制、OCR 组件或资源自动具有同一许可证。

M0 裁剪时必须为实际保留依赖建立清单，并核验其许可证。

## 基座导入原则

目标不是把完整 STranslate 原样放进仓库后长期维护全部功能。

目标步骤：

1. 固定上游快照；
2. 保留必要源代码与许可证；
3. 建立 VantreLingo 独立命名与配置目录；
4. 删除不属于 v0.1 的 UI、注册、后台任务和依赖；
5. 构建并逐步修复依赖关系；
6. 用验收文档证明“删除功能不再运行”。

浅克隆只减少 Git 历史，不算产品轻量化完成。

## 当前 M0 裁剪记录

固定 commit 已核对。完整检查副本位于 `.tmp/STranslate-v2.0.10`，不参与编译或发布。正式源码仅保留以下派生实现：

| 上游来源 | 当前源码 | 保留 / 修改 |
|---|---|---|
| `Core/HotkeyModel.cs` | `src/VantreLingo.Desktop/Infrastructure/HotkeyModel.cs` | 保留键位模型，修改命名空间并增加来源标注 |
| `Helpers/HotkeyMapper.cs` | `src/VantreLingo.Desktop/Infrastructure/HotkeyMapper.cs` | 保留 NHotkey 传统注册路径；删除低级钩子、ChefKeys、按住键、Ctrl+CC、DI 和日志耦合 |
| `Core/ISingleInstanceApp.cs` | `src/VantreLingo.Desktop/Infrastructure/SingleInstance.cs` | 保留 Mutex / named pipe 生命周期；独立标识、当前用户限制、退出取消、仅拥有者释放 |
| `Core/TranslationResultCoordinator.cs` | `src/VantreLingo.Core/Operations/OperationCoordinator.cs` | 保留操作编号及锁内检查/发布；删除词典、多结果通道和 WPF/插件依赖，加入取消所有权 |

托盘继续使用上游的 Hardcodet WPF 组件，窗口改为标准 WPF，不保留完整上游 DI 注册或主题资源。`CaptureService` 是按架构要求收敛的唯一 UI Automation 选区入口；旧 `ClipboardHelper` 的超时读取旧剪贴板路径和 `InputHelper.PrintText` 盲写路径均未纳入宿主，M1 在该入口内部增加标准 Unicode Win32 Edit 的可验证范围，并由 `NativeEditSelection` 完成受限原位替换；其他 UI Automation 控件仍只读/复制，没有引入剪贴板输入系统。该实现不复用上游盲贴路径，不新增依赖。

正式工程没有传统引擎、词典、TTS、生词本、二维码、图片重绘、SQLite、云备份、插件市场、外部 HTTP 服务或更新服务的源码、包引用和启动注册。截图/OCR 仅由 Alt+S 或用户按钮主动触发，使用系统 Windows.Media.Ocr 和已安装语言，不包含云 OCR 或后台监听。

### 实际依赖与许可

| 依赖 | 版本 | 用途 | 许可证 |
|---|---|---|---|
| Hardcodet.NotifyIcon.Wpf | 2.0.1 | 托盘 | MIT |
| NHotkey.Wpf | 4.0.0 | 主动全局热键 | Apache-2.0 |
| NHotkey（传递依赖） | 4.0.0 | Win32 热键封装 | Apache-2.0 |
| Microsoft.NETCore.App.Host.win-x64 | 构建 SDK 对应版本；本次 10.0.12 | Windows EXE 启动器 | MIT |

已核对恢复包 `.nuspec` 的许可证表达式及对应许可证文本。许可、作者和来源统一保存在 `licenses/THIRD-PARTY-NOTICES.txt`，与原 `STranslate.MIT.txt` 一同进入发布包。NuGet 依赖通过 `packages.lock.json` 和锁定恢复固定版本及内容哈希。SDK、WindowsDesktop 引用包和完整上游均留在 `.tmp`，不进入运行目录；.NET Desktop Runtime 由用户单独安装。

DPAPI 使用 .NET Desktop Runtime 自带的 `System.Security.Cryptography.ProtectedData`，没有重复引入对应 NuGet 包。正式宿主仅有两个直接 NuGet 依赖和一个传递依赖；Core 检查无第三方包；Windows 检查和桌面共享固定 Windows SDK 投影。Release 发布不携带调试符号，先在 `.tmp` 构建成功，再替换正式运行目录，避免旧依赖残留。

### Windows OCR 的 SDK 投影

OCR 将目标框架设为 `net10.0-windows10.0.19041.0`，固定 `WindowsSdkPackageVersion=10.0.19041.57`。隐式 Windows SDK targeting pack 用于 WinRT API 的 .NET 投影，发布包含 `Microsoft.Windows.SDK.NET.dll` 和 `WinRT.Runtime.dll` 等 SDK 运行时投影，不包含系统 OCR 引擎或语言模型。包自身许可链接的完整 Microsoft SDK 条款保存在 `licenses/Microsoft.Windows.SDK.LICENSE.rtf`；C#/WinRT MIT 文本在 `licenses/CsWinRT.MIT.txt`。不能把整个 Windows SDK 标为 MIT。MSIX 清单、图标和源码由本项目维护，签名证书不进入仓库。

## 第一版不追随上游更新

v0.1：

- 不内置 STranslate 自动更新；
- 不自动拉取上游 main；
- 不自动合并上游 release。

如需升级：

1. 建单独 Issue；
2. 比较固定版本与新版本；
3. 评估许可证、依赖、热键、写回与 OCR 变化；
4. 跑全部 P0 回归。
