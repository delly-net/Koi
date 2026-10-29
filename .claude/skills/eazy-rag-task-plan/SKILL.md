---
name: eazy-rag-task-plan
description: 任务计划（项目 ID: 9）——以实际任务要求为参数，按工作流生成 MD 执行计划并在 RAG 库创建任务保存
effort: high
user-invocable: true
disable-model-invocation: false
agent: general-purpose
---

# 任务计划

此技能用于根据实际任务要求，为 EAZY.RAG 知识库当前项目「Koi项目」（项目 ID: 9）自动生成 MD 格式执行计划，并在 RAG 库按当前模块创建任务保存。执行计划落库前须向用户展示确认。
创建任务前先做**模块匹配判断**：当前工作模块与本次任务要求不匹配时，读取模块清单并询问用户是否更换（自动推荐最匹配的模块），模块确定后继续后续逻辑。

## 使用方法

```
/eazy-rag-task-plan {任务要求}
```

## 功能

1. **模块确认与匹配判断**：读取本地上下文标记文件 `~/.eazyrag/context/9.json` 的 currentModuleId / currentModuleName；**未设置（无值）** → 先运行 `/eazy-rag-module-set` 设置当前工作模块，设置完成后再继续；**已设置** → 调用 `mcp__eazy-rag__list_modules`（projectId=9）读取全部模块（模块名 + 模块描述），将「任务要求」与**当前模块**的模块名/模块描述做**语义比对**——**匹配**（任务主要产出归属该模块职责）则静默继续、不打扰用户；**不匹配**（含难以判断归属的情形）则进入第 2 步询问。
2. **模块不匹配时询问更换**：以第 1 步模块清单中**语义最匹配的 1 个模块**作为**推荐项**，调用 AskUserQuestion 询问——**共 2 项**：推荐模块（选项描述带该模块描述）与保持当前模块（选项描述带当前模块描述），其余模块由用户经 Other 自由输入；用户选择**更换** → 参考 `/eazy-rag-module-set` 的写入口径把新模块写入上下文标记文件 `~/.eazyrag/context/9.json`（JSON：currentModuleId / currentModuleName / updatedAt）并以**新模块**继续；用户选择**保持当前** → **不写文件**、以当前模块继续；**两种选择均继续执行后续逻辑，不中止**。
3. **创建任务**：调用 `mcp__eazy-rag__create_task`（moduleId=当前模块Id，description=任务要求）创建任务，取得 taskId。
4. **补录模块问答**：取得 taskId 后**立即**调用 `mcp__eazy-rag__add_task_question`（taskId，question，options JSON 数组）与 `mcp__eazy-rag__answer_task_question`（taskId，questionId，answer）把第 2 步的模块询问与用户回答补录到任务问答子表——**第 2 步的询问发生在 create_task 之前（模块决定任务归属，无法后置）**，故此处补录以保证执行历史完整；模块匹配、第 2 步未发生询问时跳过本步。
5. **提取或联想关键词**：按任务要求提取或联想关键词（功能名、领域词、技术词等）。
6. **搜索关联已归档任务**：调用 `mcp__eazy-rag__search_archived_tasks`（projectId=9，keywords=关键词）搜索关联已归档任务信息，作为参考。
7. **搜索项目专属记忆库记忆点**：调用 `mcp__eazy-rag__search_memory`（projectId=9，keywords=**第 5 步同一批关键词**）检索该项目**专属记忆库**中的历史记忆点（在记忆点标题/内容中匹配，按命中关键词数降序、同分按更新时间倒序，最多 10 条；直接命中不足时用同库双向关联记忆点补全）；取回的记忆点作为后续**分析思考**的**待分析信息之一**（历史约定、既有决策、踩坑经验等）。**调用失败（项目暂无专属记忆库、无访问权、关键词全空等）或返回 0 条时静默跳过并继续**，不报错、不中断、不打扰用户。
8. **关键词工具搜索本地文件**：直接使用关键词工具（Grep/Glob）搜索本地项目文件，寻找可复用实现与既有模式。
9. **读取项目执行规范/注意事项/依赖关系**：调用 `mcp__eazy-rag__get_project`（projectId=9）→ 读取 executionSpecs / executionNotes / dependencies。
10. **分析任务要求与搜集到的信息并思考**：结合任务要求、已归档任务、本地可复用实现、项目执行规范/注意事项/依赖关系，以及**第 7 步取回的记忆点**，识别关键设计决策与不确定点。
11. **AskUser 提问**：对无法确定的信息向用户提问——**所有问题都必须提供备选项**（AskUserQuestion 带 options）；提问时调用 `mcp__eazy-rag__add_task_question`（taskId，question，options JSON 数组）将问题实时存储到任务问答子表；用户回答后调用 `mcp__eazy-rag__answer_task_question`（taskId，questionId，answer）记录回答；所有提问经操作日志在**执行历史**中展示。
12. **生成 MD 格式执行计划**：结合所有信息生成执行计划，包含章节：任务描述/任务分析/执行步骤/执行输出/结果验收/参考文档/执行规范/注意事项。
13. **更新任务并保存执行计划**：根据当前模块 Id 调用 `mcp__eazy-rag__save_execution_plan`（taskId，content）保存执行计划，**从返回的 JSON 中获取分享码（shareCode）** → 调用 `mcp__eazy-rag__update_task`（taskId，name=任务名称）回填任务名称。
14. **展示并分享**：向用户展示执行计划，**用分享码拼接完整分享查阅 URL**：`https://rag.jueyun.net/share/{分享码}`（前端访问地址已由系统配置「系统外部网址」在技能下载时填充），以 Markdown 超链接 `[分享查阅]({完整URL})` 输出；**仅展示，不执行**（不调用 `mcp__eazy-rag__confirm_execution`）。

## 注意事项

- 所有 MCP 调用必须携带正确的项目 ID：9。
- **模块匹配判断（本技能前置步骤）**：读取当前工作模块后**必须**将「任务要求」与当前模块的模块名/模块描述做语义比对；**不匹配（含难以判断归属）时必须经 AskUser 询问**并给出推荐模块，**不得静默改用其它模块**，也不得跳过判断直接创建任务。
- **模块询问先于 `create_task`（顺序例外）**：模块决定任务归属（`create_task` 的 moduleId），且任务创建后无法更换模块，故模块询问**必须发生在 `create_task` 之前**——这与下一条「先创建任务再提问」的常规顺序不同；取得 taskId 后**必须立即补录**该问答到任务问答子表（`add_task_question` + `answer_task_question`），不得遗漏（否则执行历史缺失该问答）。
- **两种选择均继续执行**：用户选择**保持当前模块**（拒绝更换）时，仍以当前模块继续执行后续逻辑，**不中止**；选择更换时写入上下文标记文件后以新模块继续。
- **所有步骤非注明需要询问或确认时，无需询问和确认，直接执行**。
- **先创建任务再提问**：问答子表按任务挂载（TaskItemId 外键），AskUser 提问前必须先调用 `create_task` 获取任务 ID（**唯一例外：模块询问先于 `create_task`，见上条**）。
- **提问必须提供备选项**：所有 AskUserQuestion 问题均须带 options，禁止无选项提问。
- **问答实时入库 + 执行历史展示**：提问 → `add_task_question`；用户回答 → `answer_task_question`；两者均实时写操作日志（Feature=taskPlanQa），管理员可在操作日志界面查看全部提问。
- 生成的执行计划必须包含 任务描述/任务分析/执行步骤/执行输出/结果验收/参考文档/执行规范/注意事项 全部章节。
- **记忆点为增强信息、不可用即跳过**：`search_memory` 检索的是项目**专属记忆库**，仅在项目已建专属记忆库且调用者可访问时才有结果；**调用失败或返回 0 条时静默跳过并继续**，不得因记忆点缺失而报错、中断或反复重试；记忆点须作为「分析思考」的待分析信息之一，不得跳过该步直接生成计划。
- **分享查阅链接**：保存执行计划后务必从 `save_execution_plan` 返回中获取分享码（shareCode），用它拼接分享查阅 URL `https://rag.jueyun.net/share/{分享码}` 并以超链接输出，供用户在浏览器查看执行计划（前端访问地址已由系统配置「系统外部网址」在技能下载时填充）。
- **仅展示，不执行**：本技能只负责生成与保存执行计划，不执行任务、不调用 `confirm_execution`；执行由用户确认后另行处理。