# Windows 安装与本地 OCR

翻译、术语、客户整理可以使用普通 `artifacts/win-x64/VantreLingo.exe`；需要 .NET 10 Desktop Runtime x64。OCR 使用 Windows.Media.Ocr，应用会检查 MSIX 包身份和已安装语言，缺少时明确提示手动输入，不上传截图、不下载模型。

## 构建 MSIX

Windows 11 x64 开发机器需要 .NET 10 SDK 和 Windows SDK（makeappx、signtool）。

```powershell
# 生成未签名包并执行 makeappx 的清单校验；仅适合构建验证
./scripts/Package.ps1

# 使用已准备的签名证书；Publisher 必须等于证书 Subject
./scripts/Package.ps1 -CertificateThumbprint YOUR_CERTIFICATE_THUMBPRINT -Publisher 'CN=Your Publisher'
# 或使用不需要命令行密码的证书文件
./scripts/Package.ps1 -CertificatePath C:\Certificates\publisher.pfx -Publisher 'CN=Your Publisher'
```

生成 `artifacts/VantreLingo-x64.msix`。脚本不创建或安装证书、不改变 Windows 信任、不自动安装或发布程序。未签名包不能直接安装；使用你信任的匹配证书签名，并按 Windows 的证书信任/侧载要求手动安装。不要将私钥、证书密码或真实 Provider 配置放入仓库。

清单固定独立包名 `VantreLingo.Desktop`、应用名 `VantreLingo`，目标 Windows 11 x64。包内包含运行文件、许可证和图标，不包含 .NET SDK、检查项目、截图、Provider key、OCR 模型或历史库。可分发包依赖 .NET 10 Desktop Runtime x64。

## OCR 使用

1. 安装受信任签名的 MSIX 包，从开始菜单启动。
2. 在设置中选择系统已安装 OCR 语言；自动模式使用用户首选语言。OCR 语言与 LLM 可翻译语言是两套能力。
3. 按 `Alt+S` 或点击“截图取字”，拖动框选，Esc 取消。
4. 本地识别结束后编辑识别文本。只有再主动点击“翻译”或“客户整理”时，文本才发送给选中 Provider；图像始终不发送。
5. 缺少语言时，可自行在 Windows 语言/可选功能中安装 OCR 能力，也可直接手动输入。应用不会代你安装或自动下载任何模型。

## 运行检查

```powershell
./scripts/Build.ps1 Verify
# 使用两个独立原生测试进程，不修改用户应用
 dotnet run --project tests/VantreLingo.Windows.Checks -c Release --no-build
```

Windows 检查需要可用的交互桌面以验证前台窗口和选区。CI 在 Windows Server runner 上执行这些检查，不把 Server 结果声称为 Windows 11 / Word / VS Code 的兼容性证据。MSIX 安装、实际离线 OCR、热键、托盘、多显示器/DPI 和目标应用矩阵按 `docs/ACCEPTANCE.md` 实机执行并记录。
