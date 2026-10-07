# S3 migration map

This maps the agreed **Windows S3 product** scope onto the new .NET solution.

| Python source | .NET implementation |
|---|---|
| `core/mllm.py`, `core/messages.py` | `Core/Messages.cs`: immutable message/block records and conversations |
| `core/engine.py`, `openai_compatible.py` | `Infrastructure/HttpLmmProvider.cs`, `ProviderCatalog.cs`, `ProviderRouter` |
| `core/codex.py` | `Infrastructure/CodexProvider.cs`, `CodexRpcClient.cs` |
| `agents/agent_s.py`, `agents/worker.py` | `Core/S3Planner.cs`, `TaskManager.cs` |
| `memory/procedural_memory.py` | `Core/Prompts.cs`: Windows-native task, reflection, and coding contracts |
| `agents/grounding.py` | `Core/GroundingService.cs`, `Windows/WindowsDesktop.cs` |
| `utils/actions.py`, `utils/formatters.py` | `Core/Actions.cs`: strict typed action parsing and validation |
| `agents/code_agent.py`, `utils/local_env.py` | `Core/CodeAgent.cs`, `Windows/PowerShellExecutor` |
| `bbon/behavior_narrator.py`, `comparative_judge.py` | `Core/BehaviorSelection.cs`: narration and validated comparative selection |
| `agent_runner.py`, `task_manager.py` | `Core/AgentTaskManager`, shared backend ownership and native lease |
| `screens.py` | `Windows/WindowsDesktop.cs`: monitors, PNG capture, physical coordinates |
| `ui_config.py` | Typed configuration, `JsonProfileStore`, `ProfileService`, DPAPI protector |
| `ui_server.py` | `Host`: ASP.NET Core API and task SSE |
| `mcp_server.py` | `Cli/McpServer.cs`: MCP stdio over the shared backend |
| `cli_app.py` | `Cli/Program.cs` |
| HTML/JS UI and approval overlay | Native `Maui` workspace, screenshots, step cards, minimization/focus restoration, approval return |

## Platform adaptations

* **Structured actions:** the planner returns one JSON decision containing a
  typed action. It does not generate an `agent.action(...)` Python expression.
  Desktop behavior is implemented through Windows APIs, not generated scripts.
* **Code agent:** optional scripting uses PowerShell 7 and .NET libraries. It
  retains incremental execution, step budgets, output feedback, and GUI
  verification. Python/Bash and Linux spreadsheet UNO commands are replaced by
  the Windows-native coding workflow. `set_cell_values` was already unavailable
  in the Python supervised Windows runner.
* **UI:** native MAUI replaces the web frontend/Tk overlay. The workspace
  minimizes before execution and returns for manual approval, keeping screenshots
  and native focus coherent. It is not a hosted copy of the old HTML.
* **Providers:** HTTP integrations use typed native adapters. Codex uses the
  app-server SDK transport directly from C#. Azure IDs are deployment IDs.
* **State and API:** .NET profiles have a new schema and separate state directory.
  Keys remain encrypted and masked. HTTP/MCP entrypoints share the same task
  supervisor, but the .NET request/response schema uses camelCase typed records.
* **Dry run:** one grounded preview is emitted. This avoids repeated calls against
  an unchanged screenshot and clearly distinguishes preview from task success.
* **Evaluation:** behavior narration/judging is available as a core library;
  OSWorld/WindowsAgentArena/AndroidWorld benchmark controllers and legacy agent
  generations are not part of the agreed product scope.

## Verification boundaries

The build and automated tests verify the managed core, HTTP/MCP behavior, native
DPAPI/OCR/PowerShell, mocked provider requests, fake Codex app-server protocol,
and native MAUI startup/controls/Unicode input. Production-provider credentials, vendor
availability, and end-to-end autonomous task accuracy depend on the selected
models and target applications. No additional live Codex generation is used by
the port's verification suite.
