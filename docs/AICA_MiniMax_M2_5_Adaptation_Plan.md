# AICA 适配 MiniMax-M2.5 计划

基于 learn-claude-code 项目的 Agent 设计模式

---

## 一、项目背景

### 1.1 当前状态
- **AICA 版本**: commit 5862b4a (8个工具完全测试完毕)
- **目标模型**: MiniMax-M2.5
- **参考项目**: learn-claude-code (12个递进式 Agent 机制)
- **核心挑战**: 从 Claude API 适配到 MiniMax API，保持 Agent 循环的稳定性

### 1.2 learn-claude-code 核心理念
```
User --> messages[] --> LLM --> response
                                  |
                        stop_reason == "tool_use"?
                       /                          \
                     yes                           no
                      |                             |
                execute tools                    return text
                append results
                loop back -----------------> messages[]
```

**关键格言**: "One loop & Bash is all you need" -- 循环不变，机制叠加

---

## 二、适配策略总览

### 2.1 四阶段适配路径

```
阶段 1: API 层适配              阶段 2: 工具调用优化
==================              ========================
- MiniMax API 接口封装          - 工具定义格式转换
- 消息格式转换                  - 工具结果处理优化
- stop_reason 映射              - 错误处理增强

阶段 3: Agent 循环强化          阶段 4: 生产级优化
========================        ========================
- TodoWrite 机制验证            - 上下文压缩策略
- 多轮对话稳定性                - 子智能体支持
- 工具调用链路追踪              - 后台任务机制
```

### 2.2 learn-claude-code 可借鉴机制

| 机制 | 优先级 | 适配难度 | 预期收益 |
|------|--------|----------|----------|
| s01: Agent Loop | P0 | 低 | 核心循环稳定性 |
| s02: Tool Use | P0 | 中 | 工具调用可靠性 |
| s03: TodoWrite | P1 | 中 | 多步任务完成率 |
| s04: Subagent | P2 | 高 | 复杂任务分解 |
| s05: Skills | P2 | 低 | 知识动态加载 |
| s06: Context Compact | P1 | 高 | 长对话支持 |
| s07: Task System | P3 | 中 | 任务持久化 |
| s08: Background Tasks | P3 | 高 | 异步操作支持 |

---

## 三、详细实施计划

### 阶段 1: API 层适配 (1-2 周)

#### 1.1 MiniMax API 客户端封装
**目标**: 创建统一的 LLM 客户端接口，屏蔽 API 差异

**实施步骤**:
1. 分析 MiniMax API 文档，对比 Claude API
2. 创建 `ILLMClient` 接口
   ```csharp
   public interface ILLMClient
   {
       Task<LLMResponse> CreateMessageAsync(
           string model,
           string system,
           List<Message> messages,
           List<Tool> tools,
           int maxTokens
       );
   }
   ```
3. 实现 `MiniMaxClient : ILLMClient`
4. 实现 `ClaudeClient : ILLMClient` (保持兼容性)

**关键映射**:
```
Claude API              MiniMax API
-----------             -----------
messages.create()   --> chat/completions
stop_reason         --> finish_reason
tool_use            --> function_call
tool_result         --> function_result
```

#### 1.2 消息格式转换器
**目标**: 双向转换 AICA 内部格式与 MiniMax 格式

**实施步骤**:
1. 创建 `MessageConverter` 类
2. 实现 `ToMiniMaxFormat()` 方法
3. 实现 `FromMiniMaxFormat()` 方法
4. 单元测试覆盖所有消息类型

**测试用例**:
- 纯文本消息
- 工具调用消息
- 工具结果消息
- 混合内容消息

#### 1.3 stop_reason 映射逻辑
**目标**: 准确判断 Agent 循环的退出条件

**映射表**:
```csharp
// Claude stop_reason -> MiniMax finish_reason
"end_turn"      -> "stop"
"tool_use"      -> "tool_calls"
"max_tokens"    -> "length"
"stop_sequence" -> "stop"
```

**实施要点**:
- 处理 MiniMax 特有的 finish_reason
- 添加降级策略（未知 reason 时的默认行为）
- 日志记录所有映射决策

---

### 阶段 2: 工具调用优化 (2-3 周)

#### 2.1 工具定义格式转换
**目标**: 将 AICA 的 8 个工具定义转换为 MiniMax 格式

**当前 AICA 工具清单**:
1. `read_file` - 读取文件
2. `write_file` - 写入文件
3. `edit_file` - 编辑文件
4. `list_files` - 列出文件
5. `search_files` - 搜索文件
6. `execute_command` - 执行命令
7. `list_projects` - 列出项目
8. `attempt_completion` - 完成任务

**转换策略**:
```csharp
// Claude Tool Schema
{
  "name": "read_file",
  "description": "...",
  "input_schema": {
    "type": "object",
    "properties": {...}
  }
}

// MiniMax Function Schema
{
  "name": "read_file",
  "description": "...",
  "parameters": {
    "type": "object",
    "properties": {...}
  }
}
```

**实施步骤**:
1. 创建 `ToolSchemaConverter` 类
2. 为每个工具编写转换逻辑
3. 验证 MiniMax 能否正确理解工具定义
4. 调整 description 以适配 MiniMax 的理解能力

#### 2.2 工具调用链路优化
**参考**: learn-claude-code s02 的 dispatch map 模式

**核心代码模式**:
```csharp
// 工具分发器
private Dictionary<string, Func<JObject, Task<string>>> _toolHandlers = new()
{
    ["read_file"] = async (input) => await ReadFileAsync(input),
    ["write_file"] = async (input) => await WriteFileAsync(input),
    // ... 其他工具
};

// Agent 循环中的工具执行
foreach (var toolCall in response.ToolCalls)
{
    if (_toolHandlers.TryGetValue(toolCall.Name, out var handler))
    {
        var result = await handler(toolCall.Input);
        toolResults.Add(new ToolResult
        {
            ToolUseId = toolCall.Id,
            Content = result
        });
    }
}
```

**优化点**:
- 统一错误处理
- 工具执行超时控制
- 工具调用日志记录
- 工具结果格式标准化

#### 2.3 错误处理增强
**目标**: 提升 Agent 在工具调用失败时的恢复能力

**错误分类**:
1. **API 错误**: 网络超时、限流、认证失败
2. **工具错误**: 文件不存在、权限不足、命令执行失败
3. **格式错误**: JSON 解析失败、参数类型错误

**处理策略**:
```csharp
try
{
    var result = await ExecuteToolAsync(toolCall);
    return new ToolResult { Success = true, Content = result };
}
catch (ToolExecutionException ex)
{
    // 将错误信息返回给 LLM，让它自行决策
    return new ToolResult
    {
        Success = false,
        Content = $"Error: {ex.Message}\nSuggestion: {ex.Suggestion}"
    };
}
catch (Exception ex)
{
    // 严重错误，中断循环
    _logger.LogError(ex, "Critical tool execution error");
    throw;
}
```

---

### 阶段 3: Agent 循环强化 (2-3 周)

#### 3.1 TodoWrite 机制实现
**参考**: learn-claude-code s03

**核心价值**: "没有计划的 agent 走哪算哪" -- 先列步骤再动手，完成率翻倍

**实施步骤**:
1. 创建 `TodoManager` 类
   ```csharp
   public class TodoManager
   {
       private List<TodoItem> _items = new();

       public string Update(List<TodoItem> items)
       {
           // 验证：同时只能有一个 in_progress
           var inProgressCount = items.Count(i => i.Status == "in_progress");
           if (inProgressCount > 1)
               throw new InvalidOperationException("Only one task can be in_progress");

           _items = items;
           return Render();
       }

       private string Render()
       {
           var sb = new StringBuilder("Current Tasks:\n");
           foreach (var item in _items)
           {
               var icon = item.Status switch
               {
                   "completed" => "[x]",
                   "in_progress" => "[>]",
                   _ => "[ ]"
               };
               sb.AppendLine($"{icon} {item.Text}");
           }
           return sb.ToString();
       }
   }
   ```

2. 添加 `todo` 工具到工具列表
3. 实现 nag reminder 机制
   ```csharp
   // 连续 3 轮不调用 todo 时注入提醒
   if (_roundsSinceTodo >= 3)
   {
       messages.Insert(0, new Message
       {
           Role = "user",
           Content = "<reminder>Update your todos to track progress.</reminder>"
       });
   }
   ```

4. 在 UI 中显示 Todo 列表

**测试场景**:
- 多步重构任务（5+ 步骤）
- 复杂功能开发（需要多个文件修改）
- Bug 修复流程（定位 -> 修复 -> 测试）

#### 3.2 多轮对话稳定性测试
**目标**: 确保 Agent 在长对话中不偏离目标

**测试维度**:
1. **轮次测试**: 10 轮、20 轮、50 轮对话
2. **工具调用密度**: 高频工具调用场景
3. **错误恢复**: 工具失败后的自我修正能力
4. **目标保持**: 是否记得初始任务目标

**测试用例设计**:
```
用例 1: 长链重构
- 初始任务: "重构 AgentExecutor.cs，拆分为 3 个类"
- 预期步骤: 分析 -> 规划 -> 创建新类 -> 迁移代码 -> 测试 -> 清理
- 验证点: 每步是否按计划执行，是否有遗漏

用例 2: 错误恢复
- 初始任务: "读取不存在的文件并处理"
- 注入错误: 文件不存在
- 预期行为: 识别错误 -> 调整策略 -> 创建文件或使用默认值

用例 3: 目标漂移检测
- 初始任务: "修复登录 Bug"
- 干扰因素: 代码中发现其他问题
- 预期行为: 记录其他问题但优先完成初始任务
```

#### 3.3 工具调用链路追踪
**目标**: 可视化 Agent 的决策过程

**实施方案**:
1. 创建 `AgentTracer` 类
   ```csharp
   public class AgentTracer
   {
       public void LogRound(int round, string thinking, List<ToolCall> tools)
       {
           _trace.Add(new TraceEntry
           {
               Round = round,
               Thinking = thinking,
               ToolCalls = tools,
               Timestamp = DateTime.Now
           });
       }

       public string ExportTrace()
       {
           // 导出为 JSON 或 Markdown
       }
   }
   ```

2. 在 UI 中展示追踪信息
   - 时间线视图
   - 工具调用树
   - 思考过程展开

3. 支持追踪导出（用于调试和分析）

---

### 阶段 4: 生产级优化 (3-4 周)

#### 4.1 上下文压缩策略
**参考**: learn-claude-code s06

**问题**: MiniMax-M2.5 的上下文窗口限制（假设 32K tokens）

**三层压缩策略**:
```
Layer 1: 工具结果摘要
- 长文件内容 -> 前 N 行 + 后 N 行
- 命令输出 -> 关键信息提取

Layer 2: 历史消息压缩
- 保留最近 5 轮完整对话
- 更早的对话 -> 摘要

Layer 3: 系统提示优化
- 动态调整 system prompt 长度
- 按需加载工具文档
```

**实施步骤**:
1. 创建 `ContextCompressor` 类
2. 实现 token 计数器（估算）
3. 实现压缩策略
4. 测试压缩后的 Agent 性能

#### 4.2 子智能体支持
**参考**: learn-claude-code s04

**应用场景**:
- 大型代码库分析（每个模块一个子 Agent）
- 并行任务执行（测试 + 文档生成）
- 专业化任务（代码审查子 Agent、重构子 Agent）

**架构设计**:
```csharp
public class SubAgent
{
    private List<Message> _messages = new(); // 独立上下文
    private ILLMClient _client;

    public async Task<string> ExecuteTaskAsync(string task)
    {
        _messages.Add(new Message { Role = "user", Content = task });

        while (true)
        {
            var response = await _client.CreateMessageAsync(...);
            // ... Agent 循环逻辑

            if (response.StopReason != "tool_use")
                return ExtractResult(response);
        }
    }
}

// 主 Agent 调用子 Agent
var subAgent = new SubAgent(_client);
var result = await subAgent.ExecuteTaskAsync("分析 AgentExecutor.cs 的复杂度");
```

#### 4.3 后台任务机制
**参考**: learn-claude-code s08

**应用场景**:
- 长时间编译任务
- 大规模测试运行
- 代码格式化

**实施方案**:
```csharp
public class BackgroundTaskManager
{
    private ConcurrentDictionary<string, Task<string>> _tasks = new();

    public string StartTask(string command)
    {
        var taskId = Guid.NewGuid().ToString();
        var task = Task.Run(async () =>
        {
            var result = await ExecuteCommandAsync(command);
            // 完成后通知 Agent
            await NotifyAgentAsync(taskId, result);
            return result;
        });

        _tasks[taskId] = task;
        return $"Task {taskId} started in background";
    }

    public string CheckTask(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            if (task.IsCompleted)
                return $"Task completed: {task.Result}";
            else
                return "Task still running";
        }
        return "Task not found";
    }
}
```

---

## 四、测试与验证

### 4.1 基准测试套件
**参考**: learn-claude-code 的测试 prompt

**测试类别**:
1. **基础工具测试** (s01-s02 级别)
   - 创建文件
   - 读取文件
   - 执行命令
   - 列出目录

2. **多步任务测试** (s03 级别)
   - 重构代码（3-5 步）
   - 创建 Python 包（多文件）
   - 修复样式问题（多文件扫描）

3. **复杂任务测试** (s04+ 级别)
   - 大型项目分析
   - 并行任务执行
   - 长对话场景（50+ 轮）

### 4.2 对比测试
**目标**: 量化 MiniMax-M2.5 与 Claude 的性能差异

**测试指标**:
| 指标 | Claude Opus | MiniMax-M2.5 | 目标 |
|------|-------------|--------------|------|
| 工具调用准确率 | 95% | ? | ≥90% |
| 多步任务完成率 | 85% | ? | ≥75% |
| 平均响应时间 | 2.5s | ? | ≤5s |
| 上下文理解准确率 | 90% | ? | ≥80% |

### 4.3 回归测试
**目标**: 确保适配不破坏现有功能

**测试清单**:
- [ ] 8 个工具全部可用
- [ ] UI 交互正常
- [ ] 错误处理正确
- [ ] 日志记录完整
- [ ] 性能无明显下降

---

## 五、风险与应对

### 5.1 技术风险

| 风险 | 影响 | 概率 | 应对措施 |
|------|------|------|----------|
| MiniMax API 不稳定 | 高 | 中 | 实现重试机制 + 降级策略 |
| 工具调用格式不兼容 | 高 | 中 | 详细测试 + 格式转换层 |
| 上下文窗口不足 | 中 | 高 | 实现压缩策略 |
| 性能不达预期 | 中 | 中 | 优化 prompt + 调整策略 |

### 5.2 进度风险
**缓解措施**:
- 采用迭代开发，每个阶段独立可用
- 保持 Claude 版本作为备份
- 及时记录问题和解决方案

---

## 六、里程碑与交付物

### 里程碑 1: API 层适配完成 (Week 2)
**交付物**:
- `MiniMaxClient` 实现
- 消息格式转换器
- 单元测试（覆盖率 ≥80%）

### 里程碑 2: 工具调用稳定 (Week 5)
**交付物**:
- 8 个工具全部适配
- 工具调用链路追踪
- 基准测试报告

### 里程碑 3: Agent 循环强化 (Week 8)
**交付物**:
- TodoWrite 机制
- 多轮对话测试通过
- 性能对比报告

### 里程碑 4: 生产级优化 (Week 12)
**交付物**:
- 上下文压缩策略
- 子智能体支持（可选）
- 完整文档和示例

---

## 七、参考资源

### 7.1 learn-claude-code 关键文档
- [s01: Agent Loop](D:\Project\AIConsProject\learn-claude-code\docs\zh\s01-the-agent-loop.md)
- [s02: Tool Use](D:\Project\AIConsProject\learn-claude-code\docs\zh\s02-tool-use.md)
- [s03: TodoWrite](D:\Project\AIConsProject\learn-claude-code\docs\zh\s03-todo-write.md)
- [s06: Context Compact](D:\Project\AIConsProject\learn-claude-code\docs\zh\s06-context-compact.md)

### 7.2 AICA 现有文档
- [AICA 开发计划](D:\Project\AIConsProject\AIHelper\docs\AICA_Development_Plan.md)
- [AICA 测试计划](D:\Project\AIConsProject\AIHelper\docs\AICA_V1_Test_Plan.md)

### 7.3 API 文档
- MiniMax API 官方文档
- Claude API 参考文档

---

## 八、总结

本计划基于 learn-claude-code 的核心理念："循环不变，机制叠加"。通过四个阶段的迭代开发，逐步将 AICA 从 Claude API 适配到 MiniMax-M2.5，同时借鉴 learn-claude-code 的成熟机制（TodoWrite、上下文压缩、子智能体等）提升 Agent 的稳定性和能力。

**核心原则**:
1. 保持 Agent 循环的简洁性
2. 工具调用的可靠性优先于功能丰富性
3. 每个阶段独立可用，支持增量交付
4. 充分测试，量化性能指标

**预期成果**:
- MiniMax-M2.5 版本的 AICA 达到 Claude 版本 75-85% 的性能
- 工具调用准确率 ≥90%
- 支持 20+ 轮稳定对话
- 为后续功能扩展（子智能体、后台任务）打下基础
