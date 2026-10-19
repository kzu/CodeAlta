using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Cursor;

/// <summary>
/// One CodeAlta session's Cursor agent ACP conversation. A prompt stays open while the CLI runs tools: each call
/// comes back as a tool call the session runs, the way a Claude Code tool does, and the text that follows is the
/// next segment. The CLI is not sent the tool result. It already has it.
/// </summary>
internal sealed class CursorAcpSession : IAsyncDisposable
{
    private static readonly JsonElement EmptySchema = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly SemaphoreSlim _turn = new(1, 1);
    private AcpPrompt? _active;
    private int _disposed;

    /// <summary>
    /// Runs the next segment of the session. The first call of a prompt starts <c>agent acp</c>; a call made while
    /// that prompt is still answering reads what the CLI writes next, and does not send another prompt.
    /// </summary>
    /// <param name="launch">The CLI launch. The working directory is the session cwd. Ignored while a prompt is in progress.</param>
    /// <param name="prompt">The user text. Ignored while a prompt is in progress.</param>
    /// <param name="earlierTranscript">Turns the CLI does not already have, sent ahead of <paramref name="prompt"/> on a new session, or null.</param>
    /// <param name="cliSessionId">An existing CLI session to load, or null to create one.</param>
    /// <param name="additionalContext">Text sent once with the first prompt of a new CLI session, or null.</param>
    /// <param name="onUpdate">Called with each assistant or reasoning chunk of this segment.</param>
    /// <param name="cancellationToken">Cancels the segment.</param>
    /// <returns>The CLI session id and the parts written in this segment.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="launch"/> or <paramref name="onUpdate"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The CLI closes the stream, returns an error, or exits before the segment is complete.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public async Task<CursorAcpSegment> RunAsync(
        CursorCliLaunch launch,
        string prompt,
        string? earlierTranscript,
        string? cliSessionId,
        string? additionalContext,
        Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(onUpdate);
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_active is not { IsComplete: false })
            {
                _active?.Stop();
                _active = null;
                _active = await AcpPrompt.StartAsync(launch, prompt, earlierTranscript, cliSessionId, additionalContext, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var segment = await _active.ReadSegmentAsync(onUpdate, cancellationToken).ConfigureAwait(false);
                if (_active.IsComplete)
                {
                    var finished = _active;
                    _active = null;
                    finished.Stop();
                }

                return segment;
            }
            catch
            {
                _active?.Stop();
                _active = null;
                throw;
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>The definition that waits for a tool call the CLI is already running, or null when this session has no such call.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="toolCall"/> is null.</exception>
    public AgentToolDefinition? ResolveTool(AgentMessagePart.ToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        return _active?.Resolve(toolCall.CallId);
    }

    /// <summary>Completes when the CLI has started the call, or null when this session has no such call.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="toolCall"/> is null.</exception>
    public Task? WhenToolStarts(AgentMessagePart.ToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        return _active?.WhenStarts(toolCall.CallId);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _active?.Stop();
        _active = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>What one prompt wrote before its next tool call, or the rest of it when the tools were done.</summary>
    /// <param name="SessionId">The CLI session id.</param>
    /// <param name="Parts">Reasoning, assistant text, and the tool calls, in the order they were written.</param>
    /// <param name="ContentIds">The content id of each part, in the same order. A tool call has none.</param>
    internal sealed record CursorAcpSegment(string SessionId, IReadOnlyList<AgentMessagePart> Parts, IReadOnlyList<string?> ContentIds);

    /// <summary>One <c>agent acp</c> process answering a single <c>session/prompt</c>.</summary>
    private sealed class AcpPrompt
    {
        /// <summary>
        /// How long sibling tool calls are gathered. ACP writes one line per call, and a message of Claude Code
        /// holds every call the model made together: a call that arrives while this window is open stays with them.
        /// </summary>
        private static readonly TimeSpan ToolBatchWindow = TimeSpan.FromMilliseconds(50);

        private const int MaximumOutputLength = 16 * 1024;
        private const int MaximumDiffLength = 32 * 1024;
        private const int MaximumArgumentsLength = 8 * 1024;
        private const int MaximumBufferedProgress = 64;

        private readonly Process _process;
        private readonly string _cwd;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly StringBuilder _stderr = new();
        private readonly object _stderrGate = new();
        private readonly object _toolsGate = new();
        private readonly Dictionary<string, Tool> _byId = new(StringComparer.Ordinal);
        private readonly List<Tool> _order = [];
        private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
        private readonly List<TextPart> _text = [];
        private readonly Channel<PromptEvent> _events = Channel.CreateUnbounded<PromptEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        private string _sessionId = string.Empty;
        private int _promptId;
        private int _reasoningParts;
        private int _assistantParts;
        private bool _produced;
        private PromptEvent? _replay;

        private AcpPrompt(Process process, string cwd)
        {
            _process = process;
            _cwd = cwd;
        }

        public bool IsComplete { get; private set; }

        public string SessionId => _sessionId;

        public static async Task<AcpPrompt> StartAsync(
            CursorCliLaunch launch,
            string prompt,
            string? earlierTranscript,
            string? cliSessionId,
            string? additionalContext,
            CancellationToken cancellationToken)
        {
            var process = CursorProcess.Start(launch, "acp", deviceFlow: false);
            var cwd = string.IsNullOrWhiteSpace(launch.WorkingDirectory) ? Environment.CurrentDirectory : launch.WorkingDirectory;
            var session = new AcpPrompt(process, cwd);
            try
            {
                await session.OpenAsync(prompt, earlierTranscript, cliSessionId, additionalContext, cancellationToken).ConfigureAwait(false);
                return session;
            }
            catch
            {
                session.Stop();
                throw;
            }
        }

        public async Task<CursorAcpSegment> ReadSegmentAsync(Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken)
        {
            while (true)
            {
                var next = await NextAsync(cancellationToken).ConfigureAwait(false);
                switch (next)
                {
                    case TextEvent text:
                        if (HasPending())
                        {
                            _replay = text;
                            return Build();
                        }

                        await AppendTextAsync(text, onUpdate, cancellationToken).ConfigureAwait(false);
                        break;
                    case ToolSignal:
                        if (!HasPending())
                        {
                            break;
                        }

                        // The calls announced together belong to this message. The text after them is the next one.
                        await CollectToolsAsync(cancellationToken).ConfigureAwait(false);
                        return Build();
                    case DoneEvent:
                        IsComplete = true;
                        return Build();
                    case FailedEvent failed:
                        throw new InvalidOperationException(failed.Message);
                }
            }
        }

        internal AgentToolDefinition? Resolve(string id)
        {
            var tool = Find(id);
            return tool is null
                ? null
                : new AgentToolDefinition(new AgentToolSpec(tool.ReportedName, "A tool run by Cursor.", EmptySchema), tool.AwaitResultAsync);
        }

        internal Task? WhenStarts(string id) => Find(id)?.Started;

        private Tool? Find(string id)
        {
            lock (_toolsGate)
            {
                return _byId.GetValueOrDefault(id);
            }
        }

        public void Stop()
        {
            try
            {
                _lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            FailOutstanding("Cursor stopped before the tool call completed.");
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                _process.Dispose();
            }
            catch (InvalidOperationException)
            {
            }
        }

        private async Task OpenAsync(string prompt, string? earlierTranscript, string? cliSessionId, string? additionalContext, CancellationToken cancellationToken)
        {
            _ = PumpErrorsAsync();
            var nextId = 0;
            await ExchangeAsync(++nextId, "initialize", writer =>
            {
                writer.WriteNumber("protocolVersion", 1);
                writer.WritePropertyName("clientCapabilities");
                writer.WriteStartObject();
                writer.WritePropertyName("fs");
                writer.WriteStartObject();
                writer.WriteBoolean("readTextFile", false);
                writer.WriteBoolean("writeTextFile", false);
                writer.WriteEndObject();
                writer.WriteBoolean("terminal", false);
                writer.WriteEndObject();
                writer.WritePropertyName("clientInfo");
                writer.WriteStartObject();
                writer.WriteString("name", "CodeAlta");
                writer.WriteString("version", "0");
                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);

            await ExchangeAsync(++nextId, "authenticate", writer => writer.WriteString("methodId", "cursor_login"), cancellationToken).ConfigureAwait(false);

            var loading = !string.IsNullOrWhiteSpace(cliSessionId);
            var opened = await ExchangeAsync(++nextId, loading ? "session/load" : "session/new", writer =>
            {
                if (loading)
                {
                    writer.WriteString("sessionId", cliSessionId);
                }

                writer.WriteString("cwd", _cwd);
                writer.WritePropertyName("mcpServers");
                writer.WriteStartArray();
                writer.WriteEndArray();
            }, cancellationToken).ConfigureAwait(false);
            _sessionId = ReadSessionId(opened) ?? cliSessionId ?? string.Empty;
            opened.Dispose();
            if (string.IsNullOrWhiteSpace(_sessionId))
            {
                throw new InvalidOperationException(Explain("Cursor ACP did not return a session id."));
            }

            var context = loading ? null : additionalContext;
            var history = loading ? null : earlierTranscript;
            _promptId = ++nextId;
            await _process.StandardInput.WriteLineAsync(Request(_promptId, "session/prompt", writer =>
            {
                writer.WriteString("sessionId", _sessionId);
                writer.WritePropertyName("prompt");
                writer.WriteStartArray();
                if (!string.IsNullOrWhiteSpace(history))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", history);
                    writer.WriteEndObject();
                }

                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", prompt);
                writer.WriteEndObject();
                if (!string.IsNullOrWhiteSpace(context))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "resource");
                    writer.WritePropertyName("resource");
                    writer.WriteStartObject();
                    writer.WriteString("uri", "codealta://system-prompt");
                    writer.WriteString("mimeType", "text/plain");
                    writer.WriteString("text", context);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            })).ConfigureAwait(false);
            _ = ReadLoopAsync();
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (true)
                {
                    var line = await _process.StandardOutput.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        FailOutstanding("Cursor stopped before the tool call completed.");
                        _events.Writer.TryWrite(new FailedEvent(Explain("Cursor ACP closed the stream during session/prompt.")));
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(line) || await HandleLineAsync(line).ConfigureAwait(false))
                    {
                        continue;
                    }

                    return;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
                FailOutstanding("Cursor stopped before the tool call completed.");
            }
            catch (Exception ex)
            {
                // The segment is waiting on the channel. Closing it without a reason would leave that wait hanging.
                FailOutstanding("Cursor stopped before the tool call completed.");
                _events.Writer.TryWrite(new FailedEvent(Explain("Cursor ACP failed: " + ex.Message)));
            }
            finally
            {
                _events.Writer.TryComplete();
            }
        }

        /// <summary>Returns false when the prompt response has been read and the loop should stop.</summary>
        private async Task<bool> HandleLineAsync(string line)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                FailOutstanding("Cursor stopped before the tool call completed.");
                _events.Writer.TryWrite(new FailedEvent(Explain("Cursor ACP sent invalid JSON: " + ex.Message)));
                return false;
            }

            string? permission = null;
            using (document)
            {
                var message = document.RootElement;
                if (message.TryGetProperty("method", out var inbound))
                {
                    switch (inbound.GetString())
                    {
                        case "session/update":
                            AcceptUpdate(message);
                            break;
                        case "session/request_permission":
                            permission = PermissionResult(message);
                            break;
                    }
                }
                else if (message.TryGetProperty("id", out var responseId) && responseId.TryGetInt32(out var response) && response == _promptId)
                {
                    FailOutstanding("Cursor stopped before the tool call completed.");
                    if (message.TryGetProperty("error", out var error))
                    {
                        _events.Writer.TryWrite(new FailedEvent(Explain("Cursor ACP session/prompt failed: " + error)));
                    }
                    else
                    {
                        _events.Writer.TryWrite(DoneEvent.Instance);
                    }

                    return false;
                }
            }

            if (permission is not null)
            {
                await _process.StandardInput.WriteLineAsync(permission).ConfigureAwait(false);
            }

            return true;
        }

        private void AcceptUpdate(JsonElement message)
        {
            if (!message.TryGetProperty("params", out var parameters))
            {
                return;
            }

            var update = parameters.TryGetProperty("update", out var nested) ? nested : parameters;
            switch (update.TryGetProperty("sessionUpdate", out var kindProperty) ? kindProperty.GetString() : null)
            {
                case "agent_message_chunk":
                case "agent_message":
                    WriteText(thought: false, TextOf(update));
                    return;
                case "agent_thought_chunk":
                case "agent_thought":
                    WriteText(thought: true, TextOf(update));
                    return;
                case "tool_call":
                case "tool_call_update":
                    var id = Identifier(update, "toolCallId");
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        return;
                    }

                    ToolFor(id).Apply(update);
                    _events.Writer.TryWrite(ToolSignal.Instance);
                    return;
                default:
                    WriteText(thought: false, DirectText(parameters));
                    return;
            }
        }

        private void WriteText(bool thought, string? text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                _events.Writer.TryWrite(new TextEvent(thought, text));
            }
        }

        private Tool ToolFor(string id)
        {
            lock (_toolsGate)
            {
                if (_byId.TryGetValue(id, out var tool))
                {
                    return tool;
                }

                tool = new Tool(id, Stop);
                _byId.Add(id, tool);
                _order.Add(tool);
                return tool;
            }
        }

        private async ValueTask<PromptEvent> NextAsync(CancellationToken cancellationToken)
        {
            if (_replay is { } replay)
            {
                _replay = null;
                return replay;
            }

            try
            {
                return await _events.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                throw new InvalidOperationException(Explain("Cursor ACP closed the stream during session/prompt."));
            }
        }

        private async Task CollectToolsAsync(CancellationToken cancellationToken)
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(ToolBatchWindow);
            while (true)
            {
                while (_events.Reader.TryRead(out var queued))
                {
                    if (Hold(queued))
                    {
                        return;
                    }
                }

                try
                {
                    var next = await _events.Reader.ReadAsync(window.Token).ConfigureAwait(false);
                    if (Hold(next))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    while (_events.Reader.TryRead(out var queued))
                    {
                        if (Hold(queued))
                        {
                            return;
                        }
                    }

                    return;
                }
                catch (ChannelClosedException)
                {
                    throw new InvalidOperationException(Explain("Cursor ACP closed the stream during session/prompt."));
                }
            }
        }

        private bool Hold(PromptEvent next)
        {
            switch (next)
            {
                case TextEvent or DoneEvent:
                    _replay = next;
                    return true;
                case FailedEvent failed:
                    throw new InvalidOperationException(failed.Message);
                default:
                    return false;
            }
        }

        private bool HasPending()
        {
            lock (_toolsGate)
            {
                foreach (var tool in _order)
                {
                    if (!_emitted.Contains(tool.Id))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private CursorAcpSegment Build()
        {
            List<Tool> pending;
            lock (_toolsGate)
            {
                pending = [];
                foreach (var tool in _order)
                {
                    if (_emitted.Add(tool.Id))
                    {
                        pending.Add(tool);
                    }
                }
            }

            var parts = new List<AgentMessagePart>(_text.Count + pending.Count);
            var ids = new List<string?>(_text.Count + pending.Count);
            foreach (var part in _text)
            {
                parts.Add(part.Thought
                    ? new AgentMessagePart.Reasoning(part.Text.ToString())
                    : new AgentMessagePart.Text(part.Text.ToString()));
                ids.Add(part.Id);
            }

            _text.Clear();
            foreach (var tool in pending)
            {
                var presented = tool.Presentation();
                parts.Add(new AgentMessagePart.ToolCall(tool.Id, presented.Name, presented.Arguments));
                ids.Add(null);
            }

            if (parts.Count == 0 && !_produced)
            {
                parts.Add(new AgentMessagePart.Text(string.Empty));
                ids.Add(NextAssistantId());
            }

            if (parts.Count > 0)
            {
                _produced = true;
            }

            return new CursorAcpSegment(_sessionId, parts, ids);
        }

        private async ValueTask AppendTextAsync(TextEvent text, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken)
        {
            TextPart part;
            if (_text.Count > 0 && _text[^1].Thought == text.Thought)
            {
                part = _text[^1];
            }
            else
            {
                part = new TextPart(text.Thought, text.Thought ? NextReasoningId() : NextAssistantId());
                _text.Add(part);
            }

            part.Text.Append(text.Text);
            await onUpdate(new AgentTurnDelta
            {
                Kind = text.Thought ? AgentContentKind.Reasoning : AgentContentKind.Assistant,
                ContentId = part.Id,
                Text = text.Text,
                IsDraft = false,
            }, cancellationToken).ConfigureAwait(false);
        }

        private string NextReasoningId()
        {
            var number = ++_reasoningParts;
            return number == 1 ? "cursor-reasoning" : $"cursor-reasoning-{number}";
        }

        private string NextAssistantId()
        {
            var number = ++_assistantParts;
            return number == 1 ? "cursor-assistant" : $"cursor-assistant-{number}";
        }

        private void FailOutstanding(string message)
        {
            Tool[] outstanding;
            lock (_toolsGate)
            {
                outstanding = [.. _byId.Values];
            }

            foreach (var tool in outstanding)
            {
                tool.Fail(message);
            }
        }

        private async Task<JsonDocument> ExchangeAsync(int id, string method, Action<Utf8JsonWriter> writeParameters, CancellationToken cancellationToken)
        {
            await _process.StandardInput.WriteLineAsync(Request(id, method, writeParameters)).ConfigureAwait(false);
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new InvalidOperationException(Explain($"Cursor ACP closed the stream during {method}."));
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.TryGetProperty("method", out var inbound))
                {
                    if (inbound.GetString() == "session/request_permission")
                    {
                        _process.StandardInput.WriteLine(PermissionResult(message));
                    }

                    document.Dispose();
                    continue;
                }

                if (message.TryGetProperty("id", out var responseId) && responseId.TryGetInt32(out var response) && response == id)
                {
                    if (message.TryGetProperty("error", out var error))
                    {
                        var description = error.ToString();
                        document.Dispose();
                        throw new InvalidOperationException(Explain($"Cursor ACP {method} failed: {description}"));
                    }

                    return document;
                }

                document.Dispose();
            }
        }

        private async Task PumpErrorsAsync()
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
                {
                    lock (_stderrGate)
                    {
                        _stderr.AppendLine(line);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
            }
        }

        private string Explain(string message)
        {
            string errors;
            lock (_stderrGate)
            {
                errors = _stderr.ToString().Trim();
            }

            return errors.Length == 0 ? message : message + " " + errors;
        }

        private static string? ReadSessionId(JsonDocument response)
        {
            if (!response.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("sessionId", out var sessionId)
                || sessionId.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return sessionId.GetString();
        }

        private static string Request(int id, string method, Action<Utf8JsonWriter> writeParameters)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteNumber("id", id);
                writer.WriteString("method", method);
                writer.WritePropertyName("params");
                writer.WriteStartObject();
                writeParameters(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string PermissionResult(JsonElement message)
        {
            var optionId = "allow-once";
            if (message.TryGetProperty("params", out var parameters) && parameters.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
            {
                string? first = null;
                foreach (var option in options.EnumerateArray())
                {
                    var id = option.TryGetProperty("optionId", out var idProperty) ? idProperty.GetString() : null;
                    var kind = option.TryGetProperty("kind", out var kindProperty) ? kindProperty.GetString() : null;
                    first ??= id;
                    if (kind is "allow_once" or "allow-once" || id is "allow-once")
                    {
                        optionId = id ?? optionId;
                        first = optionId;
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(first))
                {
                    optionId = first;
                }
            }

            var idText = message.TryGetProperty("id", out var idValue) ? idValue.GetRawText() : "0";
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WritePropertyName("id");
                using var idDocument = JsonDocument.Parse(idText);
                idDocument.RootElement.WriteTo(writer);
                writer.WritePropertyName("result");
                writer.WriteStartObject();
                writer.WritePropertyName("outcome");
                writer.WriteStartObject();
                writer.WriteString("outcome", "selected");
                writer.WriteString("optionId", optionId);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string? TextOf(JsonElement update)
            => update.TryGetProperty("content", out var content) ? BlockText(content) : null;

        private static string? BlockText(JsonElement block)
            => block.ValueKind == JsonValueKind.Object
               && block.TryGetProperty("text", out var text)
               && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;

        private static string? DirectText(JsonElement parameters)
            => parameters.TryGetProperty("content", out var direct)
               && direct.ValueKind == JsonValueKind.Object
               && direct.TryGetProperty("text", out var text)
               && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;

        private static string? Identifier(JsonElement update, string name)
        {
            if (!update.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }

        private abstract record PromptEvent;
        private sealed record TextEvent(bool Thought, string Text) : PromptEvent;
        private sealed record ToolSignal : PromptEvent
        {
            public static ToolSignal Instance { get; } = new();
        }

        private sealed record DoneEvent : PromptEvent
        {
            public static DoneEvent Instance { get; } = new();
        }

        private sealed record FailedEvent(string Message) : PromptEvent;

        private sealed class TextPart(bool thought, string id)
        {
            public bool Thought { get; } = thought;

            public string Id { get; } = id;

            public StringBuilder Text { get; } = new();
        }

        private sealed class Tool(string id, Action stop)
        {
            private readonly object _gate = new();
            private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<AgentToolResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly List<AgentToolProgressUpdate> _buffered = [];
            private AgentToolProgressHandler? _progress;
            private Task _progressTail = Task.CompletedTask;
            private string? _sent;
            private bool _presented;
            private JsonElement _arguments;
            private JsonElement? _raw;
            private string? _kind;
            private string? _status;
            private string? _title;
            private string? _path;
            private string? _command;
            private string? _query;
            private string? _url;
            private string? _output;
            private string? _oldText;
            private string? _newText;

            public string Id { get; } = id;

            public Task Started => _started.Task;

            public string ReportedName { get; private set; } = "Tool";

            public void Apply(JsonElement update)
            {
                lock (_gate)
                {
                    var title = Identifier(update, "title");
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        _title = title.Trim();
                    }

                    var kind = Identifier(update, "kind");
                    if (!string.IsNullOrWhiteSpace(kind))
                    {
                        _kind = kind;
                    }

                    var status = Identifier(update, "status");
                    if (!string.IsNullOrWhiteSpace(status))
                    {
                        _status = status;
                    }

                    if (update.TryGetProperty("rawInput", out var rawInput))
                    {
                        if (rawInput.ValueKind == JsonValueKind.Object)
                        {
                            if (rawInput.GetRawText().Length <= MaximumArgumentsLength)
                            {
                                _raw = rawInput.Clone();
                            }

                            _command = StringOf(rawInput, "command", "cmd", "commandLine") ?? _command;
                            _path ??= StringOf(rawInput, "path", "file_path", "filePath", "target_file");
                            _query ??= StringOf(rawInput, "query", "searchTerm", "pattern");
                            _url ??= StringOf(rawInput, "url", "uri");
                        }
                        else if (rawInput.ValueKind == JsonValueKind.String)
                        {
                            _command ??= rawInput.GetString();
                        }
                    }

                    if (update.TryGetProperty("locations", out var locations) && FirstPath(locations) is { } located)
                    {
                        _path = located;
                    }

                    ReadContent(update);
                    if (_output is null && ReadRawOutput(update) is { } raw)
                    {
                        NoteOutput(Bound(raw, MaximumOutputLength));
                    }

                    RefreshName();
                    if (IsTerminal(_status))
                    {
                        _result.TrySetResult(CreateResult());
                    }

                    _started.TrySetResult();
                }
            }

            public (string Name, JsonElement Arguments) Presentation()
            {
                lock (_gate)
                {
                    if (!_presented)
                    {
                        RefreshName();
                        _arguments = BuildArguments();
                        _presented = true;
                    }

                    return (ReportedName, _arguments);
                }
            }

            public void Fail(string message)
            {
                _started.TrySetResult();
                _result.TrySetResult(new AgentToolResult(false, [new AgentToolResultItem.Text(message)], message));
            }

            public async Task<AgentToolResult> AwaitResultAsync(AgentToolInvocation invocation, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(invocation);
                Attach(invocation.Progress);
                try
                {
                    var result = await _result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    Task tail;
                    lock (_gate)
                    {
                        tail = _progressTail;
                    }

                    await tail.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    stop();
                    throw;
                }
            }

            private void ReadContent(JsonElement update)
            {
                if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                var text = new StringBuilder();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var type = StringOf(item, "type");
                    if (type == "diff")
                    {
                        var path = StringOf(item, "path");
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            _path = path;
                        }

                        var oldText = StringOf(item, "oldText", "old_string");
                        var newText = StringOf(item, "newText", "new_string");
                        if (_oldText is null && _newText is null && (oldText is not null || newText is not null))
                        {
                            _oldText = Bound(oldText, MaximumDiffLength);
                            _newText = Bound(newText, MaximumDiffLength);
                        }

                        continue;
                    }

                    var block = type switch
                    {
                        "content" when item.TryGetProperty("content", out var inner) => BlockText(inner),
                        "text" => BlockText(item),
                        _ => null,
                    };
                    if (string.IsNullOrEmpty(block))
                    {
                        continue;
                    }

                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }

                    text.Append(block);
                }

                if (text.Length > 0)
                {
                    NoteOutput(Bound(text.ToString(), MaximumOutputLength));
                }
            }

            private void NoteOutput(string? output)
            {
                if (output == _output)
                {
                    return;
                }

                _output = output;
                var presented = Present();
                if (string.IsNullOrEmpty(presented))
                {
                    return;
                }

                string? delta;
                if (string.IsNullOrEmpty(_sent))
                {
                    delta = presented;
                }
                else if (presented.StartsWith(_sent, StringComparison.Ordinal))
                {
                    delta = presented[_sent.Length..];
                }
                else
                {
                    delta = null;
                }

                _sent = presented;
                if (string.IsNullOrEmpty(delta))
                {
                    return;
                }

                var update = new AgentToolProgressUpdate(delta);
                if (_progress is { } progress)
                {
                    _progressTail = Chain(_progressTail, progress, update);
                }
                else if (_buffered.Count < MaximumBufferedProgress)
                {
                    _buffered.Add(update);
                }
            }

            private string? Present()
            {
                if (string.IsNullOrEmpty(_output))
                {
                    return null;
                }

                // The Read view numbers lines the way Claude Code's Read result does (`    1: text`).
                return string.Equals(_kind, "read", StringComparison.OrdinalIgnoreCase) && !IsTerminalFailure(_status)
                    ? NumberLines(_output)
                    : _output;
            }

            private AgentToolResult CreateResult()
            {
                var output = Present();
                var items = string.IsNullOrEmpty(output)
                    ? Array.Empty<AgentToolResultItem>()
                    : new AgentToolResultItem[] { new AgentToolResultItem.Text(output) };
                if (IsTerminalFailure(_status))
                {
                    return new AgentToolResult(false, items, _status is "cancelled" or "canceled" ? "The tool call was canceled." : "The tool call failed.");
                }

                return new AgentToolResult(true, items);
            }

            private void RefreshName()
            {
                if (_presented)
                {
                    return;
                }

                ReportedName = (_kind ?? string.Empty).ToLowerInvariant() switch
                {
                    "execute" => "Bash",
                    "read" => "Read",
                    "edit" => "Edit",
                    "delete" => "Delete",
                    "move" => "Move",
                    "search" => "Grep",
                    "fetch" => "WebFetch",
                    "think" => "Think",
                    _ => "Tool",
                };
            }

            private JsonElement BuildArguments()
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    switch (ReportedName)
                    {
                        case "Bash":
                            if (_command is { Length: > 0 } command)
                            {
                                writer.WriteString("command", command);
                            }

                            if (_title is { Length: > 0 } description
                                && !string.Equals(description, "Shell", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(description, _command, StringComparison.Ordinal))
                            {
                                writer.WriteString("description", description);
                            }

                            break;
                        case "Read":
                            WritePath(writer, "file_path");
                            break;
                        case "Edit":
                            WritePath(writer, "file_path");
                            if (_oldText is not null && _newText is not null)
                            {
                                writer.WriteString("old_string", _oldText);
                                writer.WriteString("new_string", _newText);
                            }
                            else if (_newText is not null)
                            {
                                writer.WriteString("content", _newText);
                            }

                            break;
                        case "Delete":
                        case "Move":
                            WritePath(writer, "path");
                            break;
                        case "Grep":
                            if (_query is { Length: > 0 } pattern)
                            {
                                writer.WriteString("pattern", pattern);
                            }

                            WritePath(writer, "path");
                            break;
                        case "WebFetch":
                            if ((_url ?? _query ?? _path) is { Length: > 0 } url)
                            {
                                writer.WriteString("url", url);
                            }

                            break;
                        default:
                            if (_raw is { ValueKind: JsonValueKind.Object } raw)
                            {
                                foreach (var property in raw.EnumerateObject())
                                {
                                    writer.WritePropertyName(property.Name);
                                    property.Value.WriteTo(writer);
                                }
                            }

                            break;
                    }

                    writer.WriteEndObject();
                }

                using var document = JsonDocument.Parse(stream.ToArray());
                return document.RootElement.Clone();
            }

            private void WritePath(Utf8JsonWriter writer, string name)
            {
                if (_path is { Length: > 0 } path)
                {
                    writer.WriteString(name, path);
                }
            }

            private void Attach(AgentToolProgressHandler? progress)
            {
                if (progress is null)
                {
                    return;
                }

                lock (_gate)
                {
                    _progress = progress;
                    foreach (var update in _buffered)
                    {
                        _progressTail = Chain(_progressTail, progress, update);
                    }

                    _buffered.Clear();
                }
            }

            private static Task Chain(Task tail, AgentToolProgressHandler progress, AgentToolProgressUpdate update)
                => tail.ContinueWith(
                    async _ =>
                    {
                        try
                        {
                            await progress(update, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                        {
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();

            private static bool IsTerminal(string? status)
                => status is "completed" or "done" or "failed" or "error" or "cancelled" or "canceled";

            private static bool IsTerminalFailure(string? status)
                => status is "failed" or "error" or "cancelled" or "canceled";

            private static string? StringOf(JsonElement value, params string[] names)
            {
                if (value.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                foreach (var name in names)
                {
                    if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } text)
                    {
                        return text;
                    }
                }

                return null;
            }

            private static string? FirstPath(JsonElement locations)
            {
                if (locations.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var location in locations.EnumerateArray())
                {
                    if (location.ValueKind == JsonValueKind.Object
                        && location.TryGetProperty("path", out var path)
                        && path.ValueKind == JsonValueKind.String
                        && path.GetString() is { Length: > 0 } text)
                    {
                        return text;
                    }
                }

                return null;
            }

            private static string? ReadRawOutput(JsonElement update)
            {
                if (!update.TryGetProperty("rawOutput", out var raw))
                {
                    return null;
                }

                if (raw.ValueKind == JsonValueKind.String)
                {
                    return raw.GetString();
                }

                return StringOf(raw, "output", "text", "stdout", "content");
            }

            private static string? Bound(string? text, int limit)
            {
                if (text is null || text.Length <= limit)
                {
                    return text;
                }

                var length = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
                return text[..length] + "…";
            }

            private static string NumberLines(string text)
            {
                var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                if (normalized.EndsWith('\n'))
                {
                    normalized = normalized[..^1];
                }

                var lines = normalized.Split('\n');
                var builder = new StringBuilder(normalized.Length + (lines.Length * 8));
                for (var index = 0; index < lines.Length; index++)
                {
                    if (index > 0)
                    {
                        builder.Append('\n');
                    }

                    builder.Append(CultureInfo.InvariantCulture, $"{index + 1,6}");
                    builder.Append(": ");
                    builder.Append(lines[index]);
                }

                return builder.ToString();
            }
        }
    }
}
