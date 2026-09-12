# OpenClaw 和 DeepSeek MODEL 显示问题修复总结

## 版本历史

### v5.3.7 (2024-09-12)
**新增功能：**
1. 添加 Effort 显示支持
2. MODEL 获取失败时显示 "N/A" 而非 "--"
3. 修复 StatusInspection.js 中 effortSnapshot 重复声明的 bug

**修改文件：**
- `src/Harness/Services/HostedUiBridge.ModelDomFallback.js` - 添加 readCurrentEffort 函数
- `src/Harness/Services/HostedUiBridge.StatusInspection.js` - 修复重复声明，集成 effort 读取
- `src/Harness/Services/WebViewStatusInspector.Parsing.cs` - 添加 currentEffort 字段解析
- `src/Harness.Core/Services/SessionProbeModels.cs` - 添加 CurrentEffort 属性
- `src/Harness/ViewModels/StatusPresenter.cs` - 更新 FormatModelSummary 支持 effort 参数
- `src/Harness/ViewModels/MainViewModel.Fields.cs` - 修改 DefaultModelSummary 为 "N/A"
- `src/Harness/ViewModels/MainViewModel.Status.cs` - 更新 ApplyModelSummary 调用
- `src/Harness/Harness.csproj` - 版本号从 5.3.6 升级到 5.3.7

### v5.3.6 (初始修复)
**修复内容：**
1. OpenClaw MODEL 显示问题
2. DeepSeek MODEL 显示问题
3. 通用 Provider 支持

---

## 问题根因

### 1. OpenClaw 问题
**位置**: `src/Harness/Services/HostedUiBridge.ModelDomFallback.js:2`

**原因**: 
- `modelPattern` 正则表达式缺少 `openclaw` 关键词
- 导致包含 "openclaw" 的 MODEL 值被过滤，最终显示为 `--` 或空

### 2. DeepSeek 问题
**位置**: `src/Harness/Services/HostedUiBridge.ModelResolver.js:99-108`

**原因**:
- `formatModelValue` 函数对 provider/model 格式处理不够灵活
- 缺少对 deepseek 特定格式的支持

## 修复方案

### 设计原则
1. **OpenClaw** 和 **DeepSeek** - 完整支持，精确解析和格式化
2. **其他 Provider** - 通用兜底，宽松模式匹配

### 代码修改清单

#### 1. 添加 DeepSeek Backend 类型

**文件**: `src/Harness.Core/Models/HostedBackendKind.cs`
```csharp
public enum HostedBackendKind
{
    Auto = 0,
    OpenClaw = 1,
    DeepSeek = 2,  // 新增
    Generic = 3,   // 从 2 改为 3
}
```

**文件**: `src/Harness.Core/Models/HostedBackendProfile.cs`
- 添加 `DeepSeekProfile` 配置
- 更新 `For()` 方法支持 DeepSeek

#### 2. JavaScript 正则表达式增强

**文件**: `src/Harness/Services/HostedUiBridge.ModelDomFallback.js`

**主要修改**:
```javascript
// 1. 主模式：openclaw 和 deepseek 优先，其次是常见 LLM
const modelPattern = /\b(?:openclaw|deepseek|gpt|o\d|claude|gemini|...)\b/i;

// 2. 新增通用模式：匹配常见模型命名模式
const genericModelPattern = /(?:[a-z]+-)?(?:chat|turbo|vision|...)/i;

// 3. sanitizeModelLabel 增强：多级 fallback
function sanitizeModelLabel(rawText) {
    // 优先：主模式匹配
    if (modelPattern.test(normalized)) return normalized;
    
    // 次选：分段匹配
    const segment = normalized.split(/.../).find(part => modelPattern.test(part));
    if (segment) return segment;
    
    // 兜底1：通用模式匹配
    if (genericModelPattern.test(normalized)) return normalized;
    
    // 兜底2：provider/model 格式
    if (/^[a-z0-9-]+\/[a-z0-9._-]+$/i.test(normalized)) return normalized;
    
    // 兜底3：短小字母数字串（非散文）
    if (normalized.length >= 3 && /^[a-z0-9._:\/-]+$/i.test(normalized)) {
        return normalized;
    }
    
    return '';
}
```

**文件**: `src/Harness/Services/HostedUiBridge.ModelResolver.js`

**主要修改**:
```javascript
const formatModelValue = (model, provider, catalog = []) => {
    // ...
    const providerLower = cleanProvider.toLowerCase();
    
    // openclaw 和 deepseek 特殊处理：强制 provider/model 格式
    if (providerLower === 'openclaw' || providerLower === 'deepseek') {
        if (modelLower.startsWith(providerPrefix)) return cleanModel;
        return `${cleanProvider}/${cleanModel}`;
    }
    
    // 其他 provider：通用处理
    return `${cleanProvider}/${cleanModel}`;
};
```

#### 3. UI 资源字符串

**文件**: `src/Harness/Strings/en-us/Resources.resw`
```xml
<data name="SettingsBackendDeepSeek" xml:space="preserve">
    <value>DeepSeek Harness</value>
</data>
```

**文件**: `src/Harness/Strings/zh-cn/Resources.resw`
```xml
<data name="SettingsBackendDeepSeek" xml:space="preserve">
    <value>DeepSeek Harness</value>
</data>
```

**文件**: `src/Harness/Helpers/StringResources.cs`
```csharp
public static string SettingsBackendDeepSeek => Get("SettingsBackendDeepSeek");
```

**文件**: `src/Harness/ViewModels/SettingsViewModel.cs`
```csharp
public IReadOnlyList<string> BackendOptions { get; } =
[
    StringResources.SettingsBackendAuto,
    StringResources.SettingsBackendOpenClaw,
    StringResources.SettingsBackendDeepSeek,  // 新增
    StringResources.SettingsBackendGeneric,
];
```

## 修复效果

### OpenClaw
- ✅ `modelPattern` 现在包含 `openclaw` 关键词
- ✅ 所有包含 "openclaw" 的 MODEL 值都能正确识别
- ✅ 格式化为 `openclaw/model-name` 的标准格式

### DeepSeek
- ✅ 添加专用 Backend 类型 `HostedBackendKind.DeepSeek`
- ✅ `modelPattern` 包含 `deepseek` 关键词
- ✅ 特殊格式化逻辑确保 `deepseek/model-name` 格式正确

### 其他 Provider（通用兜底）
- ✅ 保留原有常见 LLM 关键词（gpt, claude, gemini 等）
- ✅ 新增 `genericModelPattern` 匹配常见模型命名模式
- ✅ 支持 `provider/model` 格式
- ✅ 接受短小字母数字串作为兜底
- ✅ 避免过滤掉不认识但合法的 MODEL 值

## 数据流

```
JavaScript Bridge (HostedUiBridge.ModelDomFallback.js)
    ↓ 从 DOM 或 app state 读取 MODEL
    ↓ modelPattern.test() - 验证 ✓ openclaw 和 deepseek 现在通过
    ↓ sanitizeModelLabel() - 清理并格式化
    ↓
JavaScript Bridge (HostedUiBridge.ModelResolver.js)
    ↓ formatModelValue() - 格式化为 provider/model
    ↓ openclaw/deepseek 特殊处理 ✓
    ↓
C# WebViewStatusInspector.Parsing.cs
    ↓ GetString(root, "currentModel") - 提取 JSON 字段
    ↓
C# MainViewModel.Status.cs
    ↓ ApplyModelSummary() - 应用到 ViewModel
    ↓
C# StatusPresenter.cs
    ↓ FormatModelSummary() - 最终显示格式
    ↓
UI 状态栏显示 ✓
```

## 验证建议

### 测试 OpenClaw
1. 启动 OpenClaw Gateway
2. 选择 Backend = "OpenClaw Gateway"
3. 验证状态栏 MODEL 显示为 `openclaw/xxx` 或 `xxx`（取决于 Gateway 返回格式）

### 测试 DeepSeek
1. 启动 DeepSeek Harness Web UI
2. 选择 Backend = "DeepSeek Harness"
3. 验证状态栏 MODEL 显示为 `deepseek/xxx` 或完整模型名

### 测试通用 Provider
1. 使用任意其他 LLM Web UI
2. 选择 Backend = "Generic Control UI"
3. 验证状态栏 MODEL 能显示（即使不是预定义的 provider）

## 相关文件

### Core Models
- `src/Harness.Core/Models/HostedBackendKind.cs` - Backend 类型枚举
- `src/Harness.Core/Models/HostedBackendProfile.cs` - Backend 配置

### JavaScript Bridge
- `src/Harness/Services/HostedUiBridge.ModelDomFallback.js` - DOM 读取和正则匹配
- `src/Harness/Services/HostedUiBridge.ModelResolver.js` - 格式化逻辑

### C# Services
- `src/Harness/Services/WebViewStatusInspector.Parsing.cs` - JSON 解析
- `src/Harness/ViewModels/MainViewModel.Status.cs` - ViewModel 状态
- `src/Harness/ViewModels/StatusPresenter.cs` - 显示格式化

### UI Resources
- `src/Harness/Strings/en-us/Resources.resw` - 英文字符串
- `src/Harness/Strings/zh-cn/Resources.resw` - 中文字符串
- `src/Harness/Helpers/StringResources.cs` - 字符串访问器
- `src/Harness/ViewModels/SettingsViewModel.cs` - 设置界面

## 总结

此次修复采用**针对性支持 + 通用兜底**的策略：
- OpenClaw 和 DeepSeek 作为主要支持对象，获得完整的识别和格式化逻辑
- 其他 Provider 通过宽松的模式匹配和多级 fallback 确保能显示
- 避免了硬编码所有可能的 LLM provider，保持了代码的可维护性
