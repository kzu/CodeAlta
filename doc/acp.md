# ACP integration

CodeAlta supports ACP backends through stdio JSON-RPC. The implementation is split between protocol/runtime support in `CodeAlta.Acp`, backend adaptation in `CodeAlta.Agent.Acp`, catalog/config metadata in `CodeAlta.Catalog`, and frontend management services under `src/CodeAlta/App`.

## Components

| Component | Role |
| --- | --- |
| `src/CodeAlta.Acp` | JSON-RPC transport, ACP protocol models, generated helpers, registry client, install resolver, and installer. |
| `src/CodeAlta.Agent.Acp` | `IAgentBackend`/`IAgentSession` adapter over an ACP client process. |
| `src/CodeAlta.Catalog/AcpBackendDefinition.cs` | Config/install definition shape for ACP agent backends. |
| `CodeAltaOwnedServices` | Registers effective ACP backends at startup and exposes `RefreshAcpBackendsAsync` for runtime refresh. |
| `AcpAgentRegistryService` and `AcpManagementService` | Registry refresh/cache, install/remove, and management snapshot data. |

ACP backends register at startup from installed definitions and `[acp.agents]` config. The TUI exposes the **ACP Agents** command (`/acp`, `/acp_agents`) for registry browsing, install/remove, and backend refresh.

## Configuration

ACP backend definitions can come from installed registry entries, `[acp.agents]` config, or the merge of both. Config entries use the `AcpBackendDefinition` fields:

```toml
[acp.agents.example]
agent_id = "example"
display_name = "Example ACP agent"
enabled = true
command = "example-agent"
args = ["--stdio"]
working_directory = "C:/work/example"
env = { EXAMPLE_MODE = "stdio" }
use_unstable = true
enable_filesystem = true
enable_terminal = true
enable_elicitation = false
```

Defaults:

| Field | Default |
| --- | --- |
| `enabled` | `true` |
| `use_unstable` | `true` |
| `enable_terminal` | `true` |
| `enable_filesystem` | `true` |
| `enable_elicitation` | `false` |

Backend ids are created as `acp:<agentId>`.

## Session behavior

`AcpAgentBackend` launches the configured command over stdio and performs ACP initialization/authentication. It supports session create/load/resume according to the server capabilities reported during initialization. `AcpAgentSession` adapts CodeAlta session operations to ACP requests and maps ACP responses/events into normalized `AgentEvent` history.

Supported behavior includes:

- initialize/auth handshake;
- session new/load/resume when capabilities allow;
- list sessions when supported;
- prompt submission;
- cancel notification for abort;
- session close during dispose when available;
- history journaling through `AcpHistoryJournal`;
- filesystem and terminal client requests when enabled;
- permission bridging;
- optional elicitation bridging when enabled and supported.

The session sends CodeAlta system/developer preamble content on the first prompt when needed, then applies model/reasoning preferences through the backend when supported by the ACP agent.

## Unsupported operations

Generic ACP sessions currently do not support:

- CodeAlta dynamic tool injection;
- live steering through `IAgentSession.SteerAsync`;
- manual compaction through `IAgentSession.CompactAsync`;
- backend session delete through `IAgentBackend.DeleteSessionAsync`.

These operations return unsupported results or throw `NotSupportedException` from the low-level session API. Docs and UI should not present them as implemented for generic ACP backends.

## Registry and install support

`AcpRegistryClient.DefaultRegistryUri` is:

```text
https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json
```

The registry cache is stored at `~/.alta/acp/registry/latest/registry.json`. Installs use:

- `~/.alta/acp/downloads/` for downloaded archives or package payloads;
- `~/.alta/acp/installs/` for resolved installs;
- `~/.alta/acp/manifests/` and `~/.alta/acp/state/` for metadata and runtime state.

`AcpInstallResolver` supports binary, `npx`, and `uvx` distribution kinds when the registry manifest and current platform allow them. Binary installs require an archive URL. Registry installs are supported only on Windows, macOS, and Linux.

The management dialog can refresh the registry, show installability, install an agent, remove installed metadata/payloads, and inspect the effective command/config that will be registered.

## Client capability bridges

When enabled by config and accepted by the ACP agent, CodeAlta bridges client requests back into host services:

- filesystem requests are resolved through CodeAlta's configured project/global context and permission handling;
- terminal requests use host terminal/shell permission paths;
- permission requests map to normalized CodeAlta permission events;
- elicitation requests are exposed only when `enable_elicitation = true` and the agent supports the unstable capability.

Capability flags should be kept narrow. Disabling filesystem, terminal, or elicitation removes the corresponding bridge from the advertised client capabilities.

## Diagnostics and safety

ACP processes are local child processes launched from user configuration or installed registry metadata. Treat their command, environment, working directory, and outputs as user-owned and potentially sensitive.

Startup failures should remain visible as provider/backend diagnostics without deleting config. Registry download/install failures should preserve existing cached/installed entries unless a user explicitly removes them.
