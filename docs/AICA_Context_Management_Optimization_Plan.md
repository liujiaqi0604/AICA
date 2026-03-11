# AICA 上下文管理系统优化方案

> 编写日期：2026-03-11
> 涉及模块：`ContextManager`、`AgentExecutor`、`ConversationStorage`、`CondenseTool`

---

## 一、现状分析

当前上下文管理由三层组成：

| 层级 | 文件 | 职责 |
|------|------|------|
| ContextManager | `Context/ContextManager.cs` | Token 估算、对话截断、优先级上下文管理 |
| AgentExecutor | `Agent/AgentExecutor.cs` | Agent 循环中的动态裁剪、安全边界、condense 处理 |
| ConversationStorage | `Storage/ConversationStorage.cs` | 会话持久化与恢复 |

经过代码审查，发现以下 **7 个可优化问题**：

---

## 二、问题清单与优化方案

### 问题 1：Token 估算精度不足

**现状：**
`EstimateTokens()` 使用固定比率（CJK ~1.5 token/字，Latin ~0.25 token/字）进行估算。

**问题：**
- 代码片段中的符号（`{`, `}`, `//`, `=>` 等）token 密度远高于普通文本，但被当作 Latin 字符按 0.25 计算
- 工具结果中的 JSON 结构（大量引号、冒号、逗号）同样被低估
- 系统提示中的 Markdown 格式符号（`###`, `**`, `` ` ``）也被低估
- 低估 token 数会导致实际发送给 LLM 的内容超出上下文窗口，触发 `context_length_exceeded` 错误后才被动裁剪

**优化方案：**

```csharp
public static int EstimateTokens(string text)
{
    if (string.IsNullOrEmpty(text)) return 0;

    int cjkCount = 0;
    int codeSymbolCount = 0;  // 新增：代码符号计数
    int otherCount = 0;

    foreach (char c in text)
    {
        if (IsCJK(c))
            cjkCount++;
        else if (IsCodeSymbol(c))
            codeSymbolCount++;  // 符号通常 1:1 映射为 token
        else
            otherCount++;
    }

    // CJK: ~1.5 tokens/char
    // 代码符号: ~0.5 tokens/char（多数符号 2-3 个合并为 1 token）
    // Latin/其他: ~0.25 tokens/char
    int tokens = (int)Math.Ceiling(
        cjkCount * 1.5 + codeSymbolCount * 0.5 + otherCount * 0.25);
    return Math.Max(1, tokens);
}

private static bool IsCodeSymbol(char c)
{
    return c == '{' || c == '}' || c == '[' || c == ']' ||
           c == '(' || c == ')' || c == '<' || c == '>' ||
           c == '"' || c == '\'' || c == ':' || c == ';' ||
           c == '=' || c == '+' || c == '-' || c == '*' ||
           c == '/' || c == '\\' || c == '|' || c == '&' ||
           c == '#' || c == '@' || c == '`' || c == '~';
}
```

**预期收益：** 减少因 token 低估导致的 `context_length_exceeded` 错误，降低被动裁剪频率。

---

### 问题 2：每次迭代重复计算全量 Token

**现状：**
`AgentExecutor` 每次迭代都执行两次全量扫描：
```csharp
// 第1次：TruncateConversation 内部遍历所有消息计算 token
conversationHistory = ContextManager.TruncateConversation(conversationHistory, conversationBudget);

// 第2次：再次遍历所有消息计算 token
int currentTokens = conversationHistory.Sum(m => Context.ContextManager.EstimateTokens(m.Content));
```

**问题：**
- 对话历史可能包含几十条消息，每条消息都要逐字符扫描
- `TruncateConversation` 内部已经计算了 `totalTokens`，但结果被丢弃，外部又重新算了一遍
- 随着对话增长，这个开销线性增加

**优化方案：**

让 `TruncateConversation` 返回截断结果和 token 总数，避免重复计算：

```csharp
/// <summary>
/// 截断结果，包含截断后的消息列表和 token 统计
/// </summary>
public class TruncateResult
{
    public List<ChatMessage> Messages { get; set; }
    public int TotalTokens { get; set; }
    public int RemovedMessageCount { get; set; }
}

public static TruncateResult TruncateConversation(
    List<ChatMessage> messages, int maxTokens, int keepRecentCount = 10)
{
    // ... 现有截断逻辑 ...
    // 在计算过程中记录 totalTokens，最终一并返回
    return new TruncateResult
    {
        Messages = result,
        TotalTokens = resultTokens,
        RemovedMessageCount = removedCount
    };
}
```

AgentExecutor 中的调用改为：
```csharp
var truncateResult = ContextManager.TruncateConversation(conversationHistory, conversationBudget);
conversationHistory = truncateResult.Messages;
int currentTokens = truncateResult.TotalTokens;  // 直接使用，无需重算
double tokenUsageRatio = (double)currentTokens / conversationBudget;
```

**预期收益：** 每次迭代减少一次全量 token 扫描，对话越长收益越明显。

---

### 问题 3：工具结果截断策略过于粗暴

**现状：**
```csharp
// 硬编码 4000 字符截断
if (resultContent != null && resultContent.Length > 4000)
{
    resultContent = resultContent.Substring(0, 4000) + "\n... (truncated)";
}
```

**问题：**
- 4000 字符是固定值，不考虑当前剩余 token 预算
- 截断位置可能切断代码块、JSON 结构或关键信息
- `read_file` 返回的代码可能在函数中间被截断，导致 LLM 误解代码结构
- `grep_search` 的结果可能在匹配项中间被截断，丢失关键上下文

**优化方案：**

引入智能截断，根据内容类型选择截断策略：

```csharp
/// <summary>
/// 智能截断工具结果，根据剩余预算和内容类型选择截断策略
/// </summary>
public static string SmartTruncateToolResult(
    string content, string toolName, int remainingTokenBudget)
{
    if (string.IsNullOrEmpty(content)) return content;

    // 单条工具结果最多占剩余预算的 30%
    int maxTokensForResult = Math.Max(500, (int)(remainingTokenBudget * 0.30));
    int estimatedTokens = EstimateTokens(content);

    if (estimatedTokens <= maxTokensForResult)
        return content;

    // 根据工具类型选择截断策略
    switch (toolName)
    {
        case "read_file":
            return TruncateCodeContent(content, maxTokensForResult);
        case "grep_search":
            return TruncateSearchResults(content, maxTokensForResult);
        case "list_dir":
            return TruncateDirectoryListing(content, maxTokensForResult);
        default:
            return TruncateToTokens(content, maxTokensForResult)
                   + $"\n... (truncated, showing ~{maxTokensForResult} tokens of ~{estimatedTokens})";
    }
}

/// <summary>
/// 代码内容截断：在完整行边界截断，保留头尾
/// </summary>
private static string TruncateCodeContent(string content, int maxTokens)
{
    var lines = content.Split('\n');
    int targetChars = (int)(maxTokens * 2.5);

    if (content.Length <= targetChars) return content;

    // 保留前 60% 和后 20% 的行
    int headChars = (int)(targetChars * 0.6);
    int tailChars = (int)(targetChars * 0.2);

    var sb = new StringBuilder();
    int charCount = 0;

    // 头部
    foreach (var line in lines)
    {
        if (charCount + line.Length > headChars) break;
        sb.AppendLine(line);
        charCount += line.Length + 1;
    }

    sb.AppendLine($"\n... ({lines.Length - CountLines(sb)} lines omitted) ...\n");

    // 尾部
    charCount = 0;
    var tailLines = new List<string>();
    for (int i = lines.Length - 1; i >= 0; i--)
    {
        if (charCount + lines[i].Length > tailChars) break;
        tailLines.Insert(0, lines[i]);
        charCount += lines[i].Length + 1;
    }
    foreach (var line in tailLines)
        sb.AppendLine(line);

    return sb.ToString();
}
```

**预期收益：** 截断后的内容保留更多有效信息，减少 LLM 因信息不完整而产生的幻觉或错误判断。

---

### 问题 4：Condense 机制完全依赖 LLM 主动触发

**现状：**
`CondenseTool` 是一个被动工具，需要 LLM 自己判断何时调用。

**问题：**
- 大多数 LLM（尤其是较小的模型如 MiniMax-M2.5）很少主动调用 condense
- 当 token 使用率达到 70-80% 时，本应是 condense 的最佳时机，但 LLM 通常不会意识到
- 等到 90% 触发安全边界时已经太晚，只能强制完成任务
- 在 70%-90% 这个区间内，系统没有任何主动干预机制

**优化方案：**

在 AgentExecutor 中增加主动 condense 触发机制：

```csharp
// 在每次迭代的 token 检查之后，安全边界检查之前
double tokenUsageRatio = (double)currentTokens / conversationBudget;

// 新增：主动 condense 触发（70%-85% 区间）
if (tokenUsageRatio > 0.70 && tokenUsageRatio <= 0.90 && !_taskState.HasCondensed)
{
    // 检查对话中是否有足够的可压缩内容（至少 5 条非系统消息）
    int compressibleMessages = conversationHistory
        .Count(m => m.Role != ChatRole.System);

    if (compressibleMessages >= 5)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[AICA] Token usage at {tokenUsageRatio:P0}, injecting condense hint");

        conversationHistory.Add(ChatMessage.System(
            $"[CONTEXT_PRESSURE] Token usage is at {tokenUsageRatio:P0}. " +
            "You should call the `condense` tool to summarize previous work and free up context space. " +
            "Include all key findings, files read/modified, and current progress in your summary."));

        _taskState.HasCondensed = true; // 防止重复注入
    }
}
```

在 `TaskState` 中新增：
```csharp
public bool HasCondensed { get; set; }
```

**预期收益：** 在 token 压力适中时主动触发压缩，避免走到安全边界被迫终止任务，让长任务能够持续执行更多步骤。

---

### 问题 5：TruncateConversation 不区分消息价值

**现状：**
截断策略是"保头保尾，裁中间"，对中间消息一视同仁地删除。

**问题：**
- 中间可能包含关键的用户指令修正（如"不要改那个文件"、"用方案 B"）
- 中间可能包含重要的工具结果（如 `read_file` 读到的关键代码片段）
- 用户的 `ask_followup_question` 回答包含重要决策信息，但可能被裁掉
- 相反，一些冗长的失败工具结果（如"Error: file not found"）价值很低，却可能因为位置靠后而被保留

**优化方案：**

引入消息权重评估，优先保留高价值消息：

```csharp
public static List<ChatMessage> TruncateConversation(
    List<ChatMessage> messages, int maxTokens, int keepRecentCount = 10)
{
    if (messages == null || messages.Count == 0) return messages;

    int totalTokens = messages.Sum(m => EstimateTokens(m.Content));
    if (totalTokens <= maxTokens) return messages;

    // 第一步：标记每条消息的保留优先级
    var scored = messages.Select((m, i) => new ScoredMessage
    {
        Index = i,
        Message = m,
        Tokens = EstimateTokens(m.Content),
        Priority = ScoreMessage(m, i, messages.Count)
    }).ToList();

    // 第二步：始终保留 system[0]、first user[1]、最近 N 条
    // 第三步：中间消息按优先级排序，从低到高移除直到满足预算
    var mustKeep = new HashSet<int>();
    // 保留头部
    for (int i = 0; i < Math.Min(2, messages.Count); i++)
        mustKeep.Add(i);
    // 保留尾部
    for (int i = Math.Max(0, messages.Count - keepRecentCount); i < messages.Count; i++)
        mustKeep.Add(i);

    // 中间消息按优先级升序排列（低优先级先移除）
    var removable = scored
        .Where(s => !mustKeep.Contains(s.Index))
        .OrderBy(s => s.Priority)
        .ToList();

    var removedIndices = new HashSet<int>();
    int tokensToFree = totalTokens - maxTokens;

    foreach (var item in removable)
    {
        if (tokensToFree <= 0) break;
        removedIndices.Add(item.Index);
        tokensToFree -= item.Tokens;
    }

    // 构建结果
    var result = new List<ChatMessage>();
    bool insertedNotice = false;
    for (int i = 0; i < messages.Count; i++)
    {
        if (removedIndices.Contains(i))
        {
            if (!insertedNotice)
            {
                result.Add(ChatMessage.System(
                    $"[NOTE: {removedIndices.Count} earlier messages were removed to fit context window.]"));
                insertedNotice = true;
            }
            continue;
        }
        result.Add(messages[i]);
    }

    return result;
}

/// <summary>
/// 评估消息的保留优先级（越高越应该保留）
/// </summary>
private static int ScoreMessage(ChatMessage msg, int index, int totalCount)
{
    int score = 0;

    // 用户消息比工具结果更重要
    if (msg.Role == ChatRole.User) score += 30;
    else if (msg.Role == ChatRole.Assistant) score += 20;
    else if (msg.Role == ChatRole.Tool) score += 10;

    // 包含用户决策的消息（followup question 的回答）
    if (msg.Role == ChatRole.User && msg.Content != null &&
        !msg.Content.StartsWith("[System") && !msg.Content.StartsWith("⚠️"))
        score += 20;

    // 失败的工具结果价值较低
    if (msg.Role == ChatRole.Tool && msg.Content != null &&
        msg.Content.StartsWith("Error:"))
        score -= 15;

    // 系统注入的纠正消息价值较低（已经起过作用）
    if (msg.Role == ChatRole.User && msg.Content != null &&
        msg.Content.Contains("⚠️"))
        score -= 10;

    // 短消息（<50 字符）通常是确认或简单回复，价值较低
    if (msg.Content != null && msg.Content.Length < 50)
        score -= 5;

    return score;
}
```

**预期收益：** 截断时优先丢弃低价值消息（失败的工具结果、系统纠正消息），保留用户决策和关键代码内容，提高截断后上下文的信息密度。

---

### 问题 6：会话恢复时丢失上下文压缩信息

**现状：**
`ConversationStorage` 保存的是原始消息列表，恢复会话时将全部 `previousMessages` 传入 `AgentExecutor`。

**问题：**
- 如果上一次会话经历了 condense，压缩后的摘要只存在于运行时的 `conversationHistory` 中，不会被持久化
- 恢复会话时加载的是原始完整消息，可能立即超出 token 预算
- `ConversationRecord.Messages` 没有记录哪些消息已经被截断或压缩过
- 长会话恢复后第一次请求就可能触发 `context_length_exceeded`

**优化方案：**

在 `ConversationRecord` 中增加上下文摘要字段，恢复时优先使用摘要：

```csharp
// ConversationRecord 新增字段
public class ConversationRecord
{
    // ... 现有字段 ...

    /// <summary>
    /// 上下文摘要：如果会话经历过 condense，保存最后一次的摘要。
    /// 恢复会话时可以用摘要替代完整历史，避免超出 token 预算。
    /// </summary>
    public string ContextSummary { get; set; }

    /// <summary>
    /// 摘要生成时的消息索引，表示摘要覆盖了 [0, SummaryUpToIndex) 的消息
    /// </summary>
    public int SummaryUpToIndex { get; set; }
}
```

保存时记录 condense 摘要：
```csharp
// ChatToolWindowControl 保存会话时，如果 AgentExecutor 产生了 condense，记录摘要
if (agentExecutor.LastCondenseSummary != null)
{
    record.ContextSummary = agentExecutor.LastCondenseSummary;
    record.SummaryUpToIndex = agentExecutor.CondenseUpToIndex;
}
```

恢复时智能加载：
```csharp
// 恢复会话时
public List<ChatMessage> BuildResumeMessages(ConversationRecord record, int tokenBudget)
{
    var messages = new List<ChatMessage>();

    if (!string.IsNullOrEmpty(record.ContextSummary))
    {
        // 有摘要：用摘要 + 摘要之后的消息
        messages.Add(ChatMessage.System(
            "[Resumed conversation] Summary of previous work:\n" + record.ContextSummary));

        // 只加载摘要之后的消息
        for (int i = record.SummaryUpToIndex; i < record.Messages.Count; i++)
        {
            messages.Add(ConvertToChat(record.Messages[i]));
        }
    }
    else
    {
        // 无摘要：加载全部消息，但用 TruncateConversation 控制预算
        foreach (var msg in record.Messages)
        {
            messages.Add(ConvertToChat(msg));
        }
        messages = ContextManager.TruncateConversation(messages, tokenBudget);
    }

    return messages;
}
```

**预期收益：** 会话恢复更平滑，避免恢复后立即触发上下文超限，长会话的恢复体验显著改善。

---

### 问题 7：ContextManager 的优先级上下文机制（ContextItem）未被使用

**现状：**
`ContextManager` 提供了完整的优先级上下文管理能力（`AddItem`、`GetContextWithinBudget`、`ContextPriority`），但在整个项目中没有任何地方调用这些方法。`AgentExecutor` 只使用了静态方法 `TruncateConversation` 和 `EstimateTokens`。

**问题：**
- 优先级上下文机制是一个设计良好但未落地的功能
- 当前所有上下文（系统提示、工具描述、工作区信息）都硬编码在 `SystemPromptBuilder` 中，无法动态调整优先级
- 如果系统提示本身就很长（当前包含大量规则），没有机制在 token 紧张时精简系统提示

**优化方案：**

将 `ContextManager` 实例化并集成到 `AgentExecutor` 中，用于管理系统提示的各个部分：

```csharp
// AgentExecutor 构造函数中初始化
private readonly ContextManager _contextManager;

public AgentExecutor(...)
{
    // ...
    _contextManager = new ContextManager(maxTokenBudget);
}

// ExecuteAsync 中使用
public async IAsyncEnumerable<AgentStep> ExecuteAsync(...)
{
    // 将系统提示拆分为多个优先级部分
    _contextManager.Clear();
    _contextManager.AddItem("base_role", basePrompt, ContextPriority.Critical);
    _contextManager.AddItem("tool_descriptions", toolDescriptions, ContextPriority.Critical);
    _contextManager.AddItem("core_rules", coreRules, ContextPriority.High);
    _contextManager.AddItem("efficiency_rules", efficiencyRules, ContextPriority.Normal);
    _contextManager.AddItem("search_strategy", searchStrategy, ContextPriority.Normal);
    _contextManager.AddItem("response_style", responseStyle, ContextPriority.Low);
    _contextManager.AddItem("workspace", workspaceContext, ContextPriority.High);

    // 根据预算动态组装系统提示
    var contextItems = _contextManager.GetContextWithinBudget();
    var systemPrompt = string.Join("\n\n",
        contextItems.OrderBy(i => GetSectionOrder(i.Key)).Select(i => i.Content));

    // ...
}
```

这需要配合 `SystemPromptBuilder` 的重构，将 `Build()` 改为返回分段内容而非单一字符串：

```csharp
public class SystemPromptBuilder
{
    /// <summary>
    /// 返回分段的系统提示，每段带有优先级标记
    /// </summary>
    public Dictionary<string, (string Content, ContextPriority Priority)> BuildSections()
    {
        return new Dictionary<string, (string, ContextPriority)>
        {
            ["base_role"] = (_basePrompt, ContextPriority.Critical),
            ["tool_descriptions"] = (_toolDescriptions, ContextPriority.Critical),
            ["core_rules"] = (_coreRules, ContextPriority.High),
            ["efficiency_rules"] = (_efficiencyRules, ContextPriority.Normal),
            // ...
        };
    }
}
```

**预期收益：** 在 token 紧张时自动精简系统提示（先丢弃 Low 优先级的 response_style，再丢弃 Normal 优先级的 efficiency_rules），为对话内容腾出更多空间。同时让已有的 ContextItem 机制真正发挥作用。

---

## 三、优先级与实施建议

| 优先级 | 问题 | 改动范围 | 预估工作量 | 理由 |
|--------|------|---------|-----------|------|
| P0 | 问题 4：主动 condense 触发 | AgentExecutor, TaskState | 小 | 改动最小，收益最大，直接解决长任务被迫中断的核心痛点 |
| P0 | 问题 2：重复 token 计算 | ContextManager, AgentExecutor | 小 | 纯重构，无风险，立即提升性能 |
| P1 | 问题 1：Token 估算精度 | ContextManager | 小 | 减少被动裁剪频率，提升整体稳定性 |
| P1 | 问题 3：智能工具结果截断 | ContextManager（新增方法） + AgentExecutor | 中 | 提升截断后信息质量，减少 LLM 幻觉 |
| P1 | 问题 5：消息价值评估 | ContextManager | 中 | 提升截断质量，但需要充分测试评分逻辑 |
| P2 | 问题 6：会话恢复优化 | ConversationStorage, AgentExecutor, ChatToolWindowControl | 中 | 改善长会话恢复体验，但涉及持久化格式变更 |
| P2 | 问题 7：ContextItem 机制落地 | SystemPromptBuilder, AgentExecutor, ContextManager | 大 | 架构层面的改进，需要重构 SystemPromptBuilder |

### 建议实施顺序

**第一阶段（快速见效）：** 问题 4 + 问题 2 + 问题 1
- 改动集中在 `ContextManager` 和 `AgentExecutor`，不涉及 UI 层和持久化层
- 可以在 1-2 天内完成并测试

**第二阶段（质量提升）：** 问题 3 + 问题 5
- 引入智能截断和消息评分，需要设计测试用例验证截断效果
- 建议用现有的综测场景验证

**第三阶段（架构优化）：** 问题 6 + 问题 7
- 涉及持久化格式变更和 SystemPromptBuilder 重构
- 建议在功能稳定后再进行

---

## 四、风险与注意事项

1. **Token 估算变更的连锁影响**：修改 `EstimateTokens` 会影响所有依赖它的截断逻辑，需要确保不会导致过度截断
2. **Condense 主动触发的时机**：70% 阈值需要根据实际模型的上下文窗口大小调整，MiniMax-M2.5 的窗口较小时可能需要降低到 60%
3. **消息评分的准确性**：`ScoreMessage` 的评分规则需要通过实际对话验证，避免误删重要消息
4. **持久化格式兼容性**：问题 6 的 `ContextSummary` 字段需要向后兼容，旧格式的会话文件不应报错
5. **测试覆盖**：建议为 `EstimateTokens`、`TruncateConversation`、`SmartTruncateToolResult` 编写单元测试，确保边界情况正确处理
