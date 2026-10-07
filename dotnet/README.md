# Agent S · .NET desktop product

A standalone C# port of the **S3 desktop product**, with a native .NET MAUI Windows
workspace. Application logic, model adapters, task supervision, screen capture,
OCR, keyboard/mouse input, persistence, HTTP services, and MCP are implemented in
.NET. There is no Python bridge.

## Build and run

Requirements: Windows 10 build 19041 or newer, .NET 10 SDK, and the `maui-windows`
workload. Native OCR uses the Windows OCR language packs. PowerShell 7 is needed
only when the optional code agent is enabled. Codex requires its native runtime
executable; configure its path in the planner panel.

From this directory:

```powershell
dotnet workload install maui-windows
dotnet build AgentS.slnx
dotnet test tests/AgentS.Tests/AgentS.Tests.csproj --no-build
dotnet run --project src/AgentS.Maui --no-build
```

The workspace starts the bundled backend on `http://127.0.0.1:8765`. The CLI and
MCP server attach to that same backend and task supervisor. The backend continues
running after a frontend closes; `agent-s shutdown` shuts it down and cancels work.

### First task

1. Select the planner provider and enter its model ID. For Codex, the UI defaults
   to **`gpt-5.6-luna` with low reasoning effort**; use **Sign in** for ChatGPT login.
2. Configure the grounding model and endpoint. This is a separate vision model,
   such as UI-TARS. Set its coordinate-space width/height in the grounding panel.
3. Save the profile. Open the target application, enter an exact task, and choose
   **Run task**. The workspace minimizes before capture so it is not the target.
4. In manual mode, the workspace returns with the grounded action preview. Approve
   one action or reject and stop. Windows focus is restored before input.

Model discovery and connection checks perform discovery only, not generation.
Dry-run mode prepares **one** grounded action and stops with a preview; it does
not pretend that an unchanged desktop has progressed through a full task.

## Projects

| Project | Responsibility |
|---|---|
| `AgentS.Core` | Immutable multimodal messages, validated settings/actions, S3 planning/reflection, grounding, coding agent, behavior selection, task state machine |
| `AgentS.Infrastructure` | OpenAI-compatible/Anthropic HTTP adapters, native Codex SDK-protocol client, bounded response handling, atomic JSON persistence |
| `AgentS.Windows` | Physical monitor capture, native Windows OCR, `SendInput`, window focus, desktop lease, DPAPI, cancellable PowerShell processes |
| `AgentS.Host` | Authenticated ASP.NET Core API, shared supervision, profile service, account login coordination, compatibility endpoints |
| `AgentS.Client` | Typed backend client and serialized local backend startup |
| `AgentS.Cli` | CLI and MCP JSON-RPC stdio server |
| `AgentS.Maui` | Native MAUI controls, compiled bindings, MVVM, profiles, screenshots, approvals, run preferences, step timeline |

Dependency direction:

```text
MAUI / CLI ── Client ── HTTP ── Host
                                  ├── Core
                                  ├── Infrastructure ── Core
                                  └── Windows ── Infrastructure / Core
```

The core does not depend on MAUI, ASP.NET, Windows, or any model vendor library.
UI, HTTP, CLI, and MCP share one execution contract and one active desktop task.

## Provider integrations

Supported provider settings cover OpenAI, Azure OpenAI, Anthropic, Gemini,
OpenRouter, DeepSeek, Qwen, Parasail, Hugging Face endpoints, LM Studio, Ollama,
vLLM, generic OpenAI-compatible endpoints, and Codex.

HTTP adapters preserve chat roles and ordered text/image blocks. Anthropic system
instructions and base64 image sources are converted explicitly. Gemini model
discovery uses its native catalog. Azure requires an explicit **deployment ID**.
Local loopback endpoints use a dummy key when no key is configured; hosted
providers accept saved keys or the standard provider environment variables.

### Codex

`CodexRpcClient` is a native C# implementation of the documented **Codex app-server
SDK protocol**, not an invocation of `codex exec` and not an unofficial executable
chat-log parser. It starts the native app-server runtime, initializes the SDK
session, reads accounts/models, and manages ephemeral threads and turns.

`CodexProvider.ConvertMessages` maps typed blocks to ordered input items. System
instructions are separate developer instructions; role-labelled history preserves
user/assistant boundaries because Codex turn inputs have no chat-role field.
Planner threads are read-only with shell tools disabled and approvals denied.
The app-server owns ChatGPT credentials and refresh. The transport is covered by
a fake native runtime, including colliding server request IDs and notifications
that arrive before the turn-start response.

Use a native runtime supporting the current v2 app-server protocol (the source
integration targets Codex 0.160). `codex` on PATH works when it resolves to the
native executable. For npm wrapper installations, select the underlying
`codex.exe` in the UI rather than a `.cmd` wrapper.

## Execution and persistence

* Desktop actions are an allowlisted, typed JSON union. Generated Python/C# is
  never interpreted to dispatch mouse or keyboard actions.
* Grounding occurs before approval. Approval binds to a task, step, and unique
  token; stale/duplicate approvals are rejected.
* Cancellation reaches model HTTP requests and Codex transports. PowerShell and
  Codex native processes are terminated with their process trees on cancellation.
  **Ctrl+Alt+Escape** stops the active task even when the workspace is minimized;
  connection status indicates if another application has reserved that shortcut.
* Monitor layout and foreground focus are checked before dispatch. Held keys and
  mouse buttons are released in `finally` blocks.
* Text input uses native Unicode input packets, preserving the user's clipboard.
* Profiles and at most 25 task histories are stored in `%LOCALAPPDATA%\AgentS.NET`.
  Provider keys are encrypted with Windows DPAPI for the current user. Public APIs
  exclude keys. Blank key fields preserve existing keys only for the same endpoint
  and provider; explicit clearing applies only to the active profile.
* Configuration and histories are written atomically. A task interrupted by a
  backend restart is restored as stopped. Active work is checkpointed before
  dispatch; completed input remains recorded as executed if cancellation follows.
  Each task retains a credential-free snapshot of its launching configuration.

Settings and histories in this directory are independent of the Python product's
existing configuration. The port uses its own typed schema.

## CLI

```powershell
dotnet run --project src/AgentS.Cli -- status
dotnet run --project src/AgentS.Cli -- profiles
dotnet run --project src/AgentS.Cli -- models --grounding
dotnet run --project src/AgentS.Cli -- run "Save the current document" --approval manual --steps 15
dotnet run --project src/AgentS.Cli -- approve <task-id> <step> <approval-token>
dotnet run --project src/AgentS.Cli -- stop <task-id>
dotnet run --project src/AgentS.Cli -- shutdown
```

Build output includes `agent-s.exe` and a `backend/` directory. Keep that directory
beside the CLI or MAUI executable when distributing build output.

### Publish the native app

```powershell
dotnet build AgentS.slnx -c Release
dotnet publish src/AgentS.Maui/AgentS.Maui.csproj -c Release --no-restore
```

The unpackaged app is produced in
`src/AgentS.Maui/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`.
It includes Windows App SDK assets and the backend. This framework-dependent
distribution needs .NET 10 and the ASP.NET Core runtime on the destination
machine; the development SDK supplies both on this machine.

## MCP

Configure a client to launch the built CLI directly. This keeps build output off
the JSON-RPC stdout stream:

```json
{
  "mcpServers": {
    "agent-s-dotnet": {
      "command": "C:\\code\\Agent-S\\dotnet\\src\\AgentS.Cli\\bin\\Debug\\net10.0-windows10.0.19041.0\\agent-s.exe",
      "args": ["mcp"]
    }
  }
}
```

Tools: `server_status`, `get_config`, `update_config`, `launch_task`, `task_status`,
`stop_task`, `approve_step`, `list_models`, `get_screenshot`, and `wait_task`.
Screenshots are returned as MCP image content. A wait is bounded to 30 seconds.

## HTTP API

All routes require `Authorization: Bearer <token>`. The shared token is in
`%LOCALAPPDATA%\AgentS.NET\backend-token`, or supplied explicitly through
`AGENT_S_DOTNET_TOKEN`.

| Route | Purpose |
|---|---|
| `GET /api/status`, `/api/providers` | Runtime/monitor status and provider presets |
| `GET /api/profiles`, `PUT /api/profiles` | Masked profiles and validated updates |
| `GET /api/tasks`, `POST /api/tasks` | Task history and launch |
| `GET /api/tasks/{id}` | Typed task snapshot |
| `POST /api/tasks/{id}/approve`, `/stop` | Bound approval and cancellation |
| `GET /api/tasks/{id}/events` | Server-sent task events |
| `GET /api/screenshot?monitor=1`, `/api/tasks/{id}/screenshot` | PNG observation |
| `POST /api/models`, `/api/connection/test` | Model discovery without generation |
| `GET /api/codex/status`, `POST /api/codex/login`, `/logout` | SDK account operations |
| `GET /api/codex/login` | Background login progress |
| `POST /api/shutdown` | Graceful backend shutdown |
| `/v1/models`, `/v1/chat/completions`, `/v1/responses` | `agent-s3` buffered compatibility adapter |

Compatibility endpoints return a completed task summary. For streaming, use the
task event endpoint. JSON field names and action shapes are documented by the
public C# records; they are a new .NET contract, not the Python API's wire schema.

Environment overrides: `AGENT_S_DOTNET_STATE`, `AGENT_S_DOTNET_URL`,
`AGENT_S_DOTNET_TOKEN`, and `AGENT_S_DOTNET_HOST`. The backend binds to loopback.

## Verification

```powershell
pwsh -File build/verify.ps1
pwsh -File build/verify.ps1 -UiSmoke
```

The test suite covers action validation, coordinate scaling, immutable history,
SDK conversion/transport, provider payloads, manual approval, cancellation,
dry-run behavior, profile credential boundaries, HTTP authentication, MCP
handshake, persistence, DPAPI, native OCR, and PowerShell process cancellation.
Providers and Codex generation are mocked. Tests do **not** call billable models.

The opt-in UI smoke executable opens the MAUI app in an isolated state directory,
finds its native UI Automation controls, checks backend authentication, captures
the application window, verifies Unicode input in its own isolated editor, and
closes its own processes. It makes no model generation request and does not launch
an agent task.

## Migration coverage

See [MIGRATION.md](MIGRATION.md) for the source-to-product map and deliberate
platform adaptations. The agreed scope is S3, Windows first, native .NET, and
native MAUI. Legacy S1/S2/S2.5 and benchmark infrastructure are outside that scope.

This product is derived from the Agent S framework by Simular AI. The repository's
[Apache-2.0 license](../LICENSE) and upstream attribution apply.
