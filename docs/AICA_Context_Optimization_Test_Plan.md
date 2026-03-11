# AICA 上下文管理优化 — 测试方案

> 编写日期：2026-03-11
> 对应改动：AICA_Context_Management_Optimization_Plan.md 全部 7 项优化
> 涉及文件：ContextManager.cs, AgentExecutor.cs, TaskState.cs, ConversationStorage.cs, SystemPromptBuilder.cs

---

## 一、单元测试（可在 tests/ 项目中自动化执行）

### 1.1 EstimateTokens — Token 估算精度（问题 1）

| 用例 ID | 输入 | 预期行为 | 验证点 |
|---------|------|---------|--------|
| ET-01 | `null` | 返回 0 | 空值边界 |
| ET-02 | `""` | 返回 0 | 空字符串边界 |
| ET-03 | `"hello world"` (11 字符，全 Latin) | 返回 `ceil(11 * 0.25) = 3` | 纯 Latin 计算 |
| ET-04 | `"你好世界"` (4 字符，全 CJK) | 返回 `ceil(4 * 1.5) = 6` | 纯 CJK 计算 |
| ET-05 | `"hello你好"` (7 字符：5 Latin + 2 CJK) | 返回 `ceil(5*0.25 + 2*1.5) = ceil(4.25) = 5` | 混合文本 |
| ET-06 | `"{}[]()=+;"` (10 字符，全代码符号) | 返回 `ceil(10 * 0.5) = 5` | 纯代码符号 |
| ET-07 | `"int x = 0;"` (10 字符：6 other + 4 符号) | 返回 `ceil(6*0.25 + 4*0.5) = ceil(3.5) = 4` | 代码混合 |
| ET-08 | 旧版对比：`"public void Foo() { return; }"` | 新版结果 > 旧版结果（因为符号权重从 0.25 提升到 0.5） | 回归对比 |
| ET-09 | 大量 JSON：`{"key":"value","arr":[1,2,3]}` | 符号占比高，估算值应明显高于 `length/4` | JSON 场景 |

### 1.2 TruncateConversationWithStats — 返回值与价值评估（问题 2 + 问题 5）

| 用例 ID | 输入 | 预期行为 | 验证点 |
|---------|------|---------|--------|
| TC-01 | 3 条短消息，预算充足 | `RemovedMessageCount = 0`，`TotalTokens` 等于手动计算值，`Messages` 原样返回 | 预算充足不截断 |
| TC-02 | `null` 输入 | 返回 `Messages = null, TotalTokens = 0, RemovedMessageCount = 0` | 空值边界 |
| TC-03 | 空列表 | 返回 `Messages = 空列表, TotalTokens = 0` | 空列表边界 |
| TC-04 | 20 条消息，预算只够 12 条 | `RemovedMessageCount > 0`，头 2 条和尾 10 条被保留，中间被移除 | 基本截断逻辑 |
| TC-05 | 中间有 1 条 `Error:` 开头的 Tool 消息 + 1 条真实 User 消息，预算只够移除 1 条 | `Error:` 消息被优先移除（score = 10-15 = -5），User 消息被保留（score = 30+20 = 50） | 价值评估排序 |
| TC-06 | 中间有 `⚠️` 开头的系统纠正消息 | 该消息 score = 30-10 = 20，低于普通 User 消息的 50，应被优先移除 | 纠正消息低优先级 |
| TC-07 | 中间有带 ToolCalls 的 Assistant 消息 | score = 20+10 = 30，高于普通 Tool 消息的 10，应被保留更久 | 结构信息保留 |
| TC-08 | 所有中间消息都是短消息（<50 字符） | 短消息 score 额外 -5，应被优先移除 | 短消息惩罚 |
| TC-09 | `TruncateConversation` 兼容性 | 调用旧方法签名，返回 `List<ChatMessage>`，行为与 `WithStats` 一致 | 向后兼容 |
| TC-10 | 消息总数 < preserveStart + preserveEnd | 走 `TruncateLongMessages` 分支，不报错 | 少量消息边界 |

### 1.3 SmartTruncateToolResult — 智能工具结果截断（问题 3）

| 用例 ID | 输入 | 预期行为 | 验证点 |
|---------|------|---------|--------|
| ST-01 | 短内容（100 字符），任意工具名 | 原样返回，不截断 | 短内容不截断 |
| ST-02 | `read_file`，5000 字符代码，剩余预算 2000 | 截断后包含头部行 + `... (N lines omitted) ...` + 尾部行 | 代码保头保尾 |
| ST-03 | `read_file`，截断后头部和尾部不重叠 | 头部行数 > 0，尾部行数 > 0，omitted > 0 | 头尾分离 |
| ST-04 | `grep_search`，200 行搜索结果，剩余预算 1000 | 截断后以完整行结束，末尾有 `... (N more lines, truncated to fit context)` | 搜索结果按行截断 |
| ST-05 | 未知工具名（如 `list_dir`），大内容 | 使用通用截断，末尾有 `(truncated, showing ~X tokens of ~Y)` | 通用回退 |
| ST-06 | `remainingTokenBudget = 100`（极小） | `maxTokensForResult = max(500, 100*0.3) = 500`，使用 500 作为下限 | 最小预算保护 |
| ST-07 | `null` 内容 | 返回 `null` | 空值边界 |
| ST-08 | `""` 空字符串 | 返回 `""` | 空字符串边界 |

### 1.4 ScoreMessage — 消息价值评分（问题 5 内部方法，通过 TC 系列间接验证）

以下通过构造特定消息序列，观察 `TruncateConversationWithStats` 的移除顺序来验证：

| 用例 ID | 消息构造 | 预期移除顺序（先移除 = 低分） | 验证点 |
|---------|---------|---------------------------|--------|
| SM-01 | Tool("Error: file not found") | 最先移除（score = 10-15 = -5） | 失败工具最低 |
| SM-02 | User("⚠️ CRITICAL: ...") | 次低（score = 30-10 = 20） | 系统纠正次低 |
| SM-03 | Assistant("ok") (短消息) | 中等偏低（score = 20-5 = 15） | 短 assistant 消息 |
| SM-04 | Tool("file content here...") (成功，长) | 中等（score = 10） | 成功工具结果 |
| SM-05 | Assistant(带 ToolCalls) | 较高（score = 20+10 = 30） | 结构信息 |
| SM-06 | User("请帮我修改这个文件") | 最高（score = 30+20 = 50） | 真实用户消息 |

### 1.5 ConversationStorage.BuildResumeMessages — 会话恢复（问题 6）

| 用例 ID | 输入 | 预期行为 | 验证点 |
|---------|------|---------|--------|
| BR-01 | `record = null` | 返回空列表 | 空值边界 |
| BR-02 | `record.Messages` 为空 | 返回空列表 | 空消息边界 |
| BR-03 | 有 ContextSummary + SummaryUpToIndex=5，共 10 条消息 | 返回 1 条 System(摘要) + 5 条消息（index 5-9） | 摘要恢复 |
| BR-04 | 有 ContextSummary 但 SummaryUpToIndex=0 | 走无摘要分支，加载全部消息 | 无效索引回退 |
| BR-05 | 无 ContextSummary，20 条消息，tokenBudget 很小 | 加载全部后调用 TruncateConversation 截断 | 无摘要截断 |
| BR-06 | SummaryUpToIndex > Messages.Count | `startIndex = Math.Min(...)` 保护，不越界 | 索引越界保护 |
| BR-07 | 消息包含各种 Role（user/assistant/tool/system/unknown） | user/assistant/tool/system 正确转换，unknown 返回 null 被跳过 | ConvertToChat 覆盖 |

### 1.6 ConversationRecord 序列化兼容性（问题 6）

| 用例 ID | 输入 | 预期行为 | 验证点 |
|---------|------|---------|--------|
| SR-01 | 新格式 JSON（含 ContextSummary 和 SummaryUpToIndex） | 反序列化成功，字段值正确 | 新格式读取 |
| SR-02 | 旧格式 JSON（不含 ContextSummary 和 SummaryUpToIndex） | 反序列化成功，ContextSummary = null，SummaryUpToIndex = 0 | 向后兼容 |
| SR-03 | 保存新格式 → 重新加载 | 往返一致 | 序列化往返 |

### 1.7 SystemPromptBuilder.BuildWithBudget — 预算感知系统提示（问题 7）

| 用例 ID | 输入 | 预期行为 | 验证点 |
|---------|------|---------|--------|
| BW-01 | tokenBudget 很大（100000） | 返回完整 GetDefaultPrompt 结果 | 预算充足走全量 |
| BW-02 | tokenBudget 极小（500） | 返回内容，至少包含 base_role（Critical），可能不含 workspace | 极限预算精简 |
| BW-03 | BuildSections 返回的 sections 数量 | 无 customInstructions 时 3 个（base_role, tool_descriptions, workspace），有时 4 个 | section 数量 |
| BW-04 | BuildSections 的 Order 字段 | base_role=0, tool_descriptions=1, workspace=2, custom_instructions=3 | 排序正确 |
| BW-05 | BuildSections 的 Priority 字段 | base_role=Critical, tool_descriptions=Critical, workspace=High, custom=High | 优先级正确 |

---

## 二、集成测试（在 VS2022 实验实例中手动执行）

### 2.1 主动 Condense 触发（问题 4）— 核心场景

**前置条件：** 安装 Debug 版 VSIX，打开一个包含多个文件的项目，打开 Debug Output 窗口（View → Output → Show output from: Debug）。

#### 测试 2.1.1：正常触发 condense hint

| 步骤 | 操作 | 预期结果 |
|------|------|---------|
| 1 | 打开 AICA Chat 窗口 | 正常打开 |
| 2 | 发送一个需要多步操作的复杂任务，例如：`"请阅读 src/AICA.Core 目录下所有 .cs 文件的内容，然后总结每个文件的职责"` | Agent 开始逐个读取文件 |
| 3 | 观察 Debug Output | 随着工具调用增多，应出现 `[AICA] Token usage at XX%, injecting condense hint` 日志 |
| 4 | 继续观察 Agent 行为 | LLM 收到 `[CONTEXT_PRESSURE]` 提示后，应调用 condense 工具 |
| 5 | 观察 Chat 窗口 | 应出现 `📝 Conversation condensed to save context space.` 提示 |
| 6 | Agent 继续执行后续步骤 | condense 后 Agent 应能继续正常工作，不会因上下文不足而中断 |

**验证点：**
- Debug Output 中出现 `injecting condense hint` 日志
- condense hint 只注入一次（不重复）
- condense 后 Agent 能继续正常执行

#### 测试 2.1.2：短对话不触发

| 步骤 | 操作 | 预期结果 |
|------|------|---------|
| 1 | 发送简单问题：`"你好"` | 正常回复，无 condense 相关日志 |
| 2 | 发送简单任务：`"读取 README.md"` | 正常执行，Debug Output 中无 `CONTEXT_PRESSURE` |

**验证点：** token 使用率低于 70% 时不注入 condense hint

#### 测试 2.1.3：condense 后 HasCondenseHinted 防重复

| 步骤 | 操作 | 预期结果 |
|------|------|---------|
| 1 | 执行复杂任务触发 condense hint | 正常触发 |
| 2 | condense 执行后，继续对话使 token 再次上升 | 不再注入第二次 `[CONTEXT_PRESSURE]` |
| 3 | 检查 Debug Output | 只有一次 `injecting condense hint` 日志 |

### 2.2 智能工具结果截断（问题 3）

#### 测试 2.2.1：大文件读取截断

| 步骤 | 操作 | 预期结果 |
|------|------|---------|
| 1 | 发送：`"读取 AgentExecutor.cs 的完整内容"` | Agent 调用 read_file |
| 2 | 观察工具结果 | 如果文件内容超出预算 30%，应被智能截断 |
| 3 | 检查截断格式 | 应包含文件头部代码 + `... (N lines omitted) ...` + 文件尾部代码 |
| 4 | Agent 后续分析 | 即使截断，Agent 应能基于头尾内容给出合理分析 |

#### 测试 2.2.2：搜索结果截断

| 步骤 | 操作 | 预期结果 |
|------|------|---------|
| 1 | 发送：`"在整个项目中搜索所有包含 'ToolResult' 的代码"` | Agent 调用 grep_search |
| 2 | 如果结果很多 | 截断后以完整行结束，末尾有 `... (N more lines, truncated to fit context)` |

### 2.3 消息价值评估截断（问题 5）

#### 测试 2.3.1：长对话中的截断质量

| 步骤 | 操作 | 预期结果 |
|------|------|---------|
| 1 | 进行一个多轮对话（至少 15 轮），包含：读文件、编辑失败重试、用户修正指令 | 对话正常进行 |
| 2 | 当截断发生时（Debug Output 中 `RemovedMessageCount > 0`） | 检查被保留的消息 |
| 3 | 验证保留优先级 | 用户的修正指令应被保留，失败的工具结果应被优先移除 |

**验证方法：** 在 Debug Output 中搜索 `[NOTE: X earlier messages were removed`，确认截断发生后 Agent 仍能正确理解上下文。

### 2.4 安全边界与 condense 协作

#### 测试 2.4.1：condense 避免安全边界触发

| 步骤 | 操作 | 预期结果 |
|------|------|---------|
| 1 | 执行一个非常复杂的任务（如分析整个项目结构并生成报告） | Agent 开始多步执行 |
| 2 | 观察 Debug Output 中的 token 使用率变化 | 应看到：70% → condense hint → condense 执行 → 使用率下降 |
| 3 | 验证是否避免了 90% 安全边界 | 如果 condense 成功，不应出现 `Safety boundary triggered` |

#### 测试 2.4.2：condense 失败后安全边界兜底

| 步骤 | 操作 | 预期结果 |
|------|------|---------|
| 1 | 如果 LLM 忽略 condense hint（不调用 condense） | token 继续增长 |
| 2 | 达到 90% | 应触发 `[SAFETY_BOUNDARY]`，强制完成任务 |
| 3 | 验证任务不会无限循环 | 最终应以 Complete 或 Error 结束 |

### 2.5 会话恢复（问题 6）

#### 测试 2.5.1：带摘要的会话恢复

| 步骤 | 操作 | 预期结果 |
|------|------|---------|
| 1 | 执行一个触发 condense 的长任务 | condense 成功执行 |
| 2 | 任务完成后，关闭 Chat 窗口 | 会话保存到磁盘 |
| 3 | 重新打开 Chat 窗口，从历史会话列表恢复该会话 | 会话恢复成功 |
| 4 | 发送后续问题：`"继续之前的工作"` | Agent 应能基于摘要理解之前的上下文 |
| 5 | 检查 Debug Output | 不应出现 `context_length_exceeded` 错误 |

#### 测试 2.5.2：无摘要的旧会话恢复

| 步骤 | 操作 | 预期结果 |
|------|------|---------|
| 1 | 恢复一个在优化之前保存的旧会话（无 ContextSummary 字段） | 恢复成功，不报错 |
| 2 | 发送新消息 | 正常工作，旧消息通过 TruncateConversation 截断 |

**验证点：** 旧格式 JSON 文件的向后兼容性

### 2.6 系统提示预算管理（问题 7）

#### 测试 2.6.1：BuildWithBudget 正常场景

此项主要通过单元测试覆盖（BW-01 到 BW-05）。集成测试中验证：

| 步骤 | 操作 | 预期结果 |
|------|------|---------|
| 1 | 正常使用 AICA，观察 Debug Output 中的系统提示长度 | 系统提示正常生成 |
| 2 | 确认所有工具描述都包含在系统提示中 | Agent 能正常调用所有工具 |

---

## 三、回归测试

确保优化不破坏现有功能：

| 用例 ID | 场景 | 操作 | 预期结果 |
|---------|------|------|---------|
| RG-01 | 基本对话 | 发送 `"你好"` | 正常回复，无异常 |
| RG-02 | 文件读取 | 发送 `"读取 README.md"` | 正常读取并显示内容 |
| RG-03 | 文件编辑 | 请求修改一个文件 | Diff 预览正常，编辑成功 |
| RG-04 | 代码搜索 | 发送 `"搜索所有使用 IAgentTool 的文件"` | grep_search 正常返回结果 |
| RG-05 | 命令执行 | 发送 `"执行 dotnet build"` | 命令正常执行 |
| RG-06 | 任务完成 | 任何任务完成后 | attempt_completion 正常调用，完成卡片正常显示 |
| RG-07 | 历史会话列表 | 打开历史会话面板 | 列表正常加载，包含项目名称 |
| RG-08 | 会话删除 | 删除一个历史会话 | 删除成功，列表刷新 |
| RG-09 | 取消操作 | 在 Agent 执行中点击 Stop | Agent 正常停止，无异常 |
| RG-10 | 多轮对话 | 连续发送 5 条不同的请求 | 每轮都正常响应，上下文连贯 |

---

## 四、边界与异常测试

| 用例 ID | 场景 | 操作 | 预期结果 |
|---------|------|------|---------|
| EX-01 | maxTokenBudget = 0 | 构造 AgentExecutor 时传入 0 | conversationBudget = 0，走 else 分支，不崩溃 |
| EX-02 | 单条消息超过整个预算 | 系统提示本身就超过 maxTokens | TruncateLongMessages 截断系统提示 |
| EX-03 | 所有中间消息 score 相同 | 构造 score 完全相同的消息序列 | 按原始顺序移除（稳定排序），不崩溃 |
| EX-04 | SmartTruncateToolResult 传入单行内容 | 只有 1 行的代码 | TruncateCodeContent 中 headLines 或 tailLines 可能为空，不崩溃 |
| EX-05 | BuildResumeMessages 传入 SummaryUpToIndex = -1 | 负数索引 | Math.Min 保护，不越界 |
| EX-06 | ConversationRecord 的 Messages 包含 Role = null 的记录 | 异常数据 | ConvertToChat 返回 null，被跳过 |

---

## 五、性能验证

| 用例 ID | 场景 | 方法 | 预期结果 |
|---------|------|------|---------|
| PF-01 | TruncateConversationWithStats 消除重复计算 | 对比优化前后，在 50 条消息的对话中测量每次迭代的 token 计算次数 | 优化后每次迭代减少 1 次全量扫描 |
| PF-02 | EstimateTokens 性能 | 对 10000 字符的混合文本调用 1000 次 | 新增 IsCodeSymbol 判断不应导致明显性能下降（<5%） |
| PF-03 | SmartTruncateToolResult 性能 | 对 100KB 的文件内容调用截断 | 应在 10ms 内完成 |

---

## 六、Debug Output 关键日志检查清单

在集成测试过程中，以下日志应在对应场景中出现：

| 日志关键字 | 对应优化 | 出现场景 |
|-----------|---------|---------|
| `[AICA] Token usage at XX%, injecting condense hint` | 问题 4 | token 使用率 70%-90% 且有 ≥5 条可压缩消息 |
| `[AICA] Condensing conversation with summary` | 问题 4 | LLM 响应 condense hint 后调用 condense 工具 |
| `[AICA] Safety boundary: context window critically full` | 安全边界 | token 使用率 >90%（condense 未生效时） |
| `[AICA] Context window exceeded, truncating history` | 问题 2 | LLM 返回 context_length_exceeded 错误 |
| `[NOTE: X earlier messages were removed` | 问题 5 | 对话历史被截断时（在消息中可见） |
