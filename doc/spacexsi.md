# Cursor and Grok Build

The Cursor agent CLI (`cursor`) and the Grok Build CLI (`grok-build`) are SpaceXSI providers. Both are turn executors over ACP on the CLI's stdin and stdout, the same kind of executor as Claude Code. Tool calls are ones the CLI already ran. CodeAlta stores no token and sets no authentication variable. Signing out writes a marker and does not run the CLI's logout. The alias `grok` remains the direct HTTP `xai` provider.

## Cursor

`type = "cursor"`. Aliases: `cursor-agent`, `cursor-cli`, `cursor_agent`. Runtime: `CodeAlta.Agent.Cursor`. Transport: the installed `agent` CLI. Default command: `agent`. `command` and `args` override the executable the way they do for Claude Code. No API key, API URL, or auth source.

Browser mode runs `agent login`. Device mode runs the same command with `NO_OPEN_BROWSER=1` and reports the https URL, plus a user code when the CLI prints one. CodeAlta does not open a browser itself. A machine that is already signed in to the CLI is signed in here. Sign-out clears only CodeAlta's view (a marker under the state root) and does not run `agent logout`.

The model list is the account inventory `agent models` prints (`id - name`). A probe publishes that inventory, and a turn passes the selected id with `--model`. When that list has one entry per reasoning effort, the picker shows the model once and the reasoning control chooses the effort. Each model is then described from models.dev when the id is a known model of Anthropic, OpenAI, xAI, Google, or Moonshot. Cursor's own ids are matched to that catalog (`claude-4.6-opus` to `claude-opus-4-6`, `claude-opus-5-thinking` to `claude-opus-5`). The list keeps the CLI's id and name. Context, input, and output token limits, tool calling, and image input are the catalog's. A model the catalog does not know, such as `auto` or `composer-2.5`, stays without those fields.

Turns use `agent acp`: initialize, authenticate with `cursor_login`, `session/new` or `session/load`, then `session/prompt`. The first prompt of a new session (`session/new`) includes CodeAlta's system prompt as an ACP embedded resource (`type` `resource`, uri `codealta://system-prompt`). The CLI appends that block after the user text as additional context, and keeps the first 20,000 characters of the resource text. A continued session (`session/load`) does not send it again. The load result omits `sessionId`; the provider keeps the id it asked to load and sends that id on `session/prompt`. `session/new` still takes the id from the result. On Windows, `agent` is a command shim. CodeAlta starts the versioned Node program that shim would start, so ACP stdio reaches the Cursor CLI. Assistant text is the streamed `session/update` chunks, and `session/request_permission` is answered so the turn is not left waiting.

```toml
[providers.cursor]
enabled = true
display_name = "Cursor"
type = "cursor"
```

In Desktop, sign in with **Sign in with the browser** or **Sign in with a device code** on the provider in **Settings > Providers**. Device login does not open a browser. It shows the address the CLI prints, and a user code when the CLI prints one. A turn shows the CLI's reasoning and the tools it runs, then the answer. Signing out here forgets CodeAlta's signed-in view and leaves the CLI login on the machine in place.

## Grok Build

`type = "grok-build"`. Aliases: `grok_build`, `grok-cli`, `grokbuild`. Runtime: `CodeAlta.Agent.Grok`. This is not the direct HTTP `xai` provider. The alias `grok` still means `xai`. Default command: `grok`. Default model, when the provider names none: `grok-4.7`. `command` and `args` override the executable. `api_key`, `api_key_env`, `api_url`, and `auth_source` are rejected.

Browser mode runs `grok login`. Device mode runs `grok login --device-auth` and reports the https URL, plus a user code when the CLI prints one. CodeAlta does not open a browser itself and does not store an API key. The child process inherits `XAI_API_KEY` when the user already set it. A machine that is already signed in is signed in here: `grok models` is the signed-in check. Its model lines are bullets (`- id`, or `* id (default)`) or `id - name`. Sign-out clears only CodeAlta's view (a marker under `auth/grok-build`) and does not run `grok logout`.

The models in the picker are the agent catalog from the ACP `initialize` result (`result._meta.modelState.availableModels`), not the broader `grok models` list. Each model keeps its `modelId` and `name`, and the reasoning efforts that entry lists. `totalContextTokens` in that entry's `_meta` is the context window. `acceptsImages` and `toolCall`, when the CLI sends them, are image input and tool calling. models.dev fills whichever of the context, input, and output limits, tool calling, and image input the CLI left out, for an id it lists under xAI — or under Anthropic, OpenAI, Google, or Moonshot when the model is one of those. A window the CLI reported is kept. A turn passes the model with `--model` and the effort with `--effort` (`minimal`, `low`, `medium`, `high`, `xhigh`, `max`). `AgentReasoningEffort.None` omits `--effort`.

Turns start `grok --no-auto-update agent --always-approve stdio`. `--no-auto-update` is a top-level flag and comes before `agent`. The handshake is initialize, then authenticate, then `session/new` or `session/load`, then `session/prompt`. Authenticate uses `xai.api_key` when the CLI offers it and `XAI_API_KEY` is set, otherwise `cached_token` when that method is offered. A CLI that offers only `grok.com` is not signed in. The first prompt of a new session includes CodeAlta's system prompt as an ACP embedded resource (`codealta://system-prompt`). A later prompt loads the CLI session recorded for this CodeAlta session (`session/load`), including after CodeAlta attaches the session again, and sends only the new user text: the CLI already has the earlier turns. A fork or a rewind starts a new CLI session, because that transcript still holds what was cut, and is given the conversation CodeAlta kept. The CLI runs its own tools. CodeAlta shows each call and waits for the CLI to finish it; it does not run the tool a second time. Assistant text is the streamed `session/update` chunks, and `session/request_permission` is answered allow-once so the turn is not left waiting. A JSON-RPC response whose id is not the number of the request in progress, such as the CLI's `skills-reload` result, is not the end of the prompt.

```toml
[providers.grok-build]
enabled = true
display_name = "Grok Build"
type = "grok-build"
model = "grok-4.7"
```

Install the CLI from its own installer, then run `grok login` once if you have not signed in from a terminal. Signing out here forgets CodeAlta's signed-in view and leaves the CLI login on the machine in place.

## Rules

- Do not give either provider its own `IAgentSession`, journal, or timeline events, and do not execute its tool calls a second time.
- Do not store a CLI token, and do not set or remove an authentication variable in the child's environment.
- Claude Code has no login flow. Cursor and Grok Build do: the login is the CLI's own.
- Qualify Grok Build changes with `CodeAlta.Tests.GrokCli`, whose messages have the shape the provider speaks. The scripted CLI requires `--no-auto-update` before `agent`, then `--always-approve`, then `stdio`.
