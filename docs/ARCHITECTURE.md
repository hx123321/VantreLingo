# VantreLingo v0.1 — 技术架构

本文以 [PRD](PRD.zh-CN.md) 为功能边界。第一版没有业务系统 API、后台 Agent 或自建服务端。

## 1. 技术决策

| 项目 | 决策 |
|---|---|
| 基座 | STranslate v2.0.10 / `2a75118fe0fabc1135a619f4393eb3a8d936c5cd` 固定快照 |
| 客户端 | .NET 10 + WPF |
| 目标 | Windows 11 x64 |
| UI | 托盘 + 一个复用工具浮窗 + 设置窗口 |
| 文本捕获 | UI Automation 优先；受控模拟复制作为兼容路径 |
| 写回 | 原目标快照 + 应用前重验证 + 一次性完整写回 |
| LLM | 单一 OpenAI-compatible Adapter，可保存多个 Provider 配置 |
| 本地数据 | 版本化 JSON；密钥 DPAPI CurrentUser |
| OCR | Windows.Media.Ocr，本地执行 |
| 客户正文 | 默认内存处理，不默认落盘 |
| 更新 | v0.1 不启用上游自动更新 |

## 2. 单一处理管线

```text
热键 / 浮窗输入 / 手动粘贴 / 截图 OCR
                 │
          Capture + Snapshot
                 │
      Read / Write / Inquiry Intent
                 │
       Language Routing Service
                 │
       Style Conflict Validation
                 │
 Glossary Match + Protected Tokens
                 │
              LLM
                 │
     Structured Response Validation
          ┌──────┴────────┐
          │               │
     Translation       Inquiry Data
          │               │
 Review / Fast Gate    Evidence Check
          │               │
 Revalidate Target     Missing Rules
          │               │
 One-shot Writeback    Review / Export
          │
 Commit Language Memory
```

任何 OS 写入动作都在 LLM 返回之后由本地代码决定。LLM 不拥有键鼠控制权。

## 3. 模块职责

### 3.1 CaptureService

负责：

- 明确选区读取；
- 获取前台进程、窗口、控件；
- 能获取时保存 UI Automation selection/range；
- 生成不可变 SelectionSnapshot。

禁止：

- 无选区时读取“上一次剪贴板”当作当前输入；
- 扫描整个窗口全文；
- 常驻上传窗口内容。

### 3.2 OperationCoordinator

负责：

- request id；
- CancellationToken；
- 最新请求所有权；
- 防止迟到请求更新结果；
- 写回串行化。

原则：

> 旧请求可以完成释放资源，但不得覆盖新请求的 UI、剪贴板、语言记忆或写回。

### 3.3 LanguageRoutingService

输入：

- source detection；
- 用户手动 source/target；
- fixed target；
- lastForeignLanguage。

输出：

- effective source；
- effective target；
- 是否允许提交新的 lastForeignLanguage。

不得混入风格规则。

### 3.4 PromptComposer

负责：

- 场景；
- 专业方向；
- 语气；
- 性格/表达；
- 篇幅；
- 忠实度；
- 术语；
- 保护项；
- 自定义提示。

在调用 LLM 前完成本地冲突检查。

### 3.5 GlossaryService

负责：

- JSON CRUD；
- schema migration；
- active/draft；
- 上下文匹配；
- longest-match；
- forbidden variants；
- 译后术语验证。

不负责自动同步 ERP 或站点。

### 3.6 LlmClient

第一版提供一个 OpenAI-compatible Adapter。

职责：

- endpoint；
- API key；
- model；
- timeout；
- cancellation；
- structured response parsing。

禁止：

- 自行控制键盘鼠标；
- Provider 失败时无提示自动换供应商；
- 把正文写日志。

### 3.7 ProtectedContentValidator

保护：

- 数字；
- 货币；
- 百分比；
- 单位；
- 尺寸；
- SKU/型号；
- 日期；
- URL/email；
- 代码；
- 模板变量；
- 否定；
- 条件；
- 主体；
- 重要术语。

它只能做有限规则校验，不能宣称证明语义完全等价。

### 3.8 WritebackCoordinator

职责：

- 读取 SelectionSnapshot；
- 写回前再次验证窗口、控件、原文或 selection；
- 仅在全部安全门通过时写回；
- 完整结果一次性提交；
- 写回失败保持原文。

禁止：

- `Ctrl+A`；
- 先删除后等 LLM；
- 流式逐字覆盖；
- 向“当前焦点”盲贴；
- 自动 Enter/发送。

### 3.9 InquiryService

LLM 输出候选结构，本地代码完成：

- evidence substring 校验；
- 多产品拆分；
- 状态归一化；
- 条件缺项规则；
- 冲突保留。

禁止：

- 自动报价；
- 自动补事实；
- 自动访问 URL；
- 自动发送消息。

### 3.10 OcrService

负责：

- 截图生命周期；
- Windows.Media.Ocr；
- 可用语言枚举；
- OCR 结果返回。

禁止云端图片上传。

## 4. SelectionSnapshot

建议结构：

```csharp
record SelectionSnapshot(
    long OperationId,
    int ProcessId,
    nint TopLevelWindow,
    string? AutomationId,
    string? ControlType,
    string OriginalText,
    string OriginalHash,
    SelectionLocator? Locator,
    DateTimeOffset CapturedAt
);
```

`SelectionLocator` 必须表达“如何验证同一个选区”，而不只是“当前有焦点”。

快速模式无法建立足够可靠 Locator 时，降级为不自动写回。

## 5. 快速模式状态机

```text
Captured
  ↓
Requesting
  ↓
LLM Completed
  ↓
Content Validation
  ├─ fail → Preserve Original
  ↓
Target Revalidation
  ├─ fail → Preserve Original
  ↓
Atomic Apply
  ├─ fail → Preserve/Recover
  ↓
Commit Language Memory
  ↓
Done
```

只有 `Atomic Apply` 成功后才提交与写作相关的副作用。

## 6. 审查模式

审查窗口允许：

- 查看原文；
- 查看译文；
- 修改译文；
- Diff；
- 风险列表；
- 替换；
- 追加；
- 复制。

审查按钮触发写回时仍然使用原 SelectionSnapshot 进行验证。

如果用户等待期间已经编辑了原文，应阻止原位替换，并允许复制当前译文。

## 7. 风格数据模型

示例：

```json
{
  "id": "customer-business",
  "scene": "customer_email",
  "domain": "oem_manufacturing",
  "tone": "professional_business",
  "persona": "direct_practical",
  "length": "preserve",
  "fidelity": "high",
  "custom_prompt": null,
  "quick_mode_approved": true
}
```

冲突图由代码维护，例如：

```text
formal_document x humorous        = deny
formal_document x lively          = deny
strict_fidelity x heavy_shorten   = deny
humorous x literal_rigid          = deny
```

正式文件 preset 还需要自动开启 ProtectedContentValidator 的严格级别。

## 8. 术语模型

建议：

```json
{
  "schema_version": 1,
  "terms": [
    {
      "id": "term-001",
      "status": "draft",
      "source_language": "zh-CN",
      "target_language": "en",
      "source": "气垫梳",
      "target": "cushion brush",
      "contexts": ["product", "oem"],
      "direction": "forward",
      "forbidden_variants": [],
      "notes": ""
    }
  ]
}
```

不将 VANTRE 的全部 ERP 字典硬编码进项目第一版。先提供本地词库能力，词条逐步人工确认。

## 9. 客户整理数据结构

建议 LLM 只返回候选：

```json
{
  "customer": {},
  "products": [],
  "candidate_missing": [],
  "candidate_conflicts": []
}
```

本地层再生成最终 UI 模型：

```json
{
  "field": "destination",
  "status": "missing",
  "evidence": null,
  "reason": "Freight quote requested but destination is absent",
  "priority": "critical"
}
```

### 9.1 Evidence 规则

如果 LLM 声称字段为 known，但 evidence 不能在原文定位：

- 不接受 known；
- 改为 ambiguous 或 missing；
- UI 标记模型证据校验失败。

### 9.2 多产品

每个产品对象必须有独立证据范围。

禁止把 A 产品数量套给 B 产品。

## 10. 配置与存储

建议：

```text
%LocalAppData%/VantreLingo/
  settings.json
  providers.json
  glossary.json
  presets.json
```

API key 不直接出现在 `providers.json` 明文中。

客户正文、OCR 截图、翻译历史默认不建立数据库。

## 11. OCR

第一版使用 Windows.Media.Ocr：

- 按需加载；
- 使用系统已安装语言；
- 不捆绑大型模型；
- 不上传截图。

需要在实际开发阶段验证 MSIX / package identity、签名与语言能力。不能因为代码可调用 API 就假设所有裸 EXE 环境都工作。

## 12. 从 STranslate 保留

优先复用：

- 单实例；
- 托盘生命周期；
- 必要 WPF 窗口基础；
- 全局热键框架；
- 截图捕获；
- Operation/Coordinator 的并发隔离思想；
- 已有替换翻译入口中可复用部分；
- 插件接口中确有必要的轻量契约。

## 13. 从 STranslate 删除/停用

v0.1 不需要的模块应逐项移除注册、UI、资源与依赖：

- 多传统翻译引擎市场；
- 词典；
- TTS；
- 生词本；
- 鼠标常驻划词；
- 剪贴板自动监听；
- 二维码；
- 图片翻译覆盖；
- WebDAV/云备份；
- SQLite 翻译历史；
- 插件市场；
- 上游自动更新；
- 与这些功能绑定的后台启动项。

不能只隐藏 UI 而保留后台启动。

## 14. 代码组织原则

不要为了“干净架构”拆成大量小项目或本地微服务。

首版建议保持：

- 一个桌面宿主；
- 一个轻量 Core；
- 必要测试项目。

模块边界用接口和目录表达即可。

## 15. 安全与隐私边界

- API key DPAPI；
- debug log 默认不含正文；
- crash log 清理正文；
- 无遥测；
- 无默认历史；
- 无自动云同步；
- 无业务 API；
- 无自动发信；
- 无自动建单；
- 无后台 Agent。
