using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Grok;

/// <summary>
/// Options for a Grok Build CLI provider. Construction does not start a process.
/// </summary>
public sealed class GrokModelProviderRuntimeOptions
{
    /// <summary>Gets the provider key.</summary>
    public required string ProviderKey { get; init; }

    /// <summary>Gets the display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the CLI executable. Empty selects <c>grok</c>.</summary>
    public string? Command { get; init; }

    /// <summary>Gets the arguments placed before <c>login</c>, <c>models</c>, and <c>agent stdio</c>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Gets the profile state root that holds the signed-out marker.</summary>
    public required string StateRootPath { get; init; }

    /// <summary>Gets the default model id.</summary>
    public string? DefaultModelId { get; init; }

    /// <summary>Gets whether this provider is the default.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Gets whether the provider is enabled.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Gets the models.dev catalog that describes limits and abilities the CLI left out.</summary>
    public ModelsDevCatalogService? ModelCatalog { get; init; }
}

/// <summary>
/// Model provider that runs turns through the Grok Build CLI ACP session the user is signed in to.
/// The CLI calls the model and runs its tools. Each turn of CodeAlta is the next assistant message of that session.
/// </summary>
public sealed class GrokModelProviderRuntime : IAgentModelProviderRuntime
{
    /// <summary>The provider type of the configuration (<c>type = "grok-build"</c>).</summary>
    public const string ProviderType = "grok-build";

    /// <summary>The protocol family recorded for sessions of this provider.</summary>
    public const string ProtocolFamily = "grok-build";

    /// <summary>The model used when the provider definition names none.</summary>
    public const string DefaultModel = "grok-4.7";

    private readonly GrokModelProviderRuntimeOptions _options;
    private readonly GrokModelCatalog _modelCatalog;
    private readonly GrokTurnExecutor _turnExecutor;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrokModelProviderRuntime"/> class.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The provider key or state root is empty.</exception>
    public GrokModelProviderRuntime(GrokModelProviderRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StateRootPath);
        _options = options;
        Descriptor = CreateDescriptor(options);
        RuntimeDescriptor = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = ProtocolFamily,
            ProviderKey = Descriptor.ProviderId.Value,
            DisplayName = Descriptor.DisplayName,
            TransportKind = AgentTransportKind.GrokBuildCli,
            IsDefault = options.IsDefault,
            Profile = new AgentProviderProfile
            {
                SupportsReasoningEffort = true,
                SupportsToolResultImages = false,
            },
        };
        _modelCatalog = new GrokModelCatalog(options);
        _turnExecutor = new GrokTurnExecutor(options);
    }

    /// <inheritdoc />
    public ModelProviderDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; }

    /// <inheritdoc />
    public IModelProviderModelCatalog? ModelCatalog => _modelCatalog;

    /// <summary>
    /// Creates the descriptor without starting the CLI.
    /// </summary>
    /// <param name="options">The provider runtime options.</param>
    /// <returns>The provider descriptor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static ModelProviderDescriptor CreateDescriptor(GrokModelProviderRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var providerKey = options.ProviderKey.Trim();
        var displayName = string.IsNullOrWhiteSpace(options.DisplayName) ? "Grok Build" : options.DisplayName.Trim();
        return new ModelProviderDescriptor(new ModelProviderId(providerKey), displayName, ProviderType)
        {
            IsDefault = options.IsDefault,
            IsEnabled = options.IsEnabled,
            DefaultModelId = string.IsNullOrWhiteSpace(options.DefaultModelId) ? DefaultModel : options.DefaultModelId.Trim(),
        };
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!Descriptor.IsEnabled)
        {
            return new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Disabled };
        }

        if (GrokSignInView.IsDismissed(_options.StateRootPath, _options.ProviderKey))
        {
            return SignedOut();
        }

        try
        {
            var signedIn = await GrokAgentLogin.IsAuthenticatedAsync(Launch(null), cancellationToken).ConfigureAwait(false);
            if (!signedIn)
            {
                return SignedOut();
            }
        }
        catch (InvalidOperationException ex)
        {
            return Unavailable(ex.Message);
        }

        IReadOnlyList<AgentModelInfo> models;
        try
        {
            models = await _modelCatalog.ListModelsAsync(RuntimeDescriptor, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Unavailable(ex.Message);
        }

        return new ModelProviderProbeResult
        {
            ProviderId = Descriptor.ProviderId,
            Availability = ModelProviderAvailability.Ready,
            Models = models,
            SelectedModelId = Descriptor.DefaultModelId,
            StatusMessage = models.Count == 0
                ? "Grok Build is signed in."
                : $"Grok Build is signed in. {models.Count} models.",
        };
    }

    /// <inheritdoc />
    public IModelProviderTurnExecutor CreateTurnExecutor() => _turnExecutor;

    /// <inheritdoc />
    public AgentRuntimeProviderRegistration CreateProviderRegistration()
        => new()
        {
            Provider = RuntimeDescriptor,
            TurnExecutor = _turnExecutor,
            ModelCatalog = _modelCatalog,
        };

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _turnExecutor.DisposeAsync();

    private ModelProviderProbeResult SignedOut()
        => new()
        {
            ProviderId = Descriptor.ProviderId,
            Availability = ModelProviderAvailability.Failed,
            StatusMessage = "Grok Build is not signed in.",
            ErrorCategory = "grok-build-signed-out",
        };

    private ModelProviderProbeResult Unavailable(string message)
        => new()
        {
            ProviderId = Descriptor.ProviderId,
            Availability = ModelProviderAvailability.Failed,
            StatusMessage = message,
            ErrorCategory = "grok-build-unavailable",
        };

    private GrokCliLaunch Launch(string? workingDirectory)
        => GrokCliLaunch.From(_options.Command, _options.Arguments, workingDirectory);

    private sealed class GrokModelCatalog(GrokModelProviderRuntimeOptions options) : IModelProviderModelCatalog
    {
        public async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(ModelProviderRuntimeDescriptor provider, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(provider);
            var models = await GrokAgentLogin.ListModelsAsync(GrokCliLaunch.From(options.Command, options.Arguments, null), cancellationToken).ConfigureAwait(false);
            return SubscriptionModelCapabilities.Describe(models, options.ModelCatalog);
        }
    }

    private sealed class GrokTurnExecutor(GrokModelProviderRuntimeOptions options) : IModelProviderTurnExecutor, IAgentProviderToolHost, IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, GrokAcpSession> _sessions = new(StringComparer.Ordinal);
        private int _disposed;

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(onUpdate);
            if (GrokSignInView.IsDismissed(options.StateRootPath, options.ProviderKey))
            {
                throw new InvalidOperationException("Grok Build is signed out in CodeAlta.");
            }

            return RunAsync(request, onUpdate, cancellationToken);
        }

        public void AttachRun(AgentProviderRunContext context)
        {
            // The CLI asks for permission on its own stream. The turn answers allow-once.
            ArgumentNullException.ThrowIfNull(context);
        }

        public AgentToolDefinition? ResolveTool(string sessionId, AgentMessagePart.ToolCall toolCall, AgentToolDefinition? registered)
        {
            ArgumentNullException.ThrowIfNull(toolCall);
            return _sessions.TryGetValue(sessionId, out var session) ? session.ResolveTool(toolCall) : null;
        }

        public Task? WhenToolStarts(string sessionId, AgentMessagePart.ToolCall toolCall)
        {
            ArgumentNullException.ThrowIfNull(toolCall);
            return _sessions.TryGetValue(sessionId, out var session) ? session.WhenToolStarts(toolCall) : null;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var session in _sessions.Values)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task<AgentTurnResponse> RunAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var arguments = GrokAgentLogin.AgentArguments(options.Arguments, request.ModelId, request.ReasoningEffort);
            // The CLI session id is the transcript on disk. Attaching this CodeAlta session again does not
            // forget it. A fork or a rewind still points at a transcript that holds what was cut.
            var cliSessionId = request.State.ProviderBranch is null ? BlankToNull(request.State.ProviderSessionId) : null;
            var segment = await SessionFor(request.SessionId).RunAsync(
                GrokCliLaunch.From(options.Command, arguments, request.WorkingDirectory),
                LastUserText(request.Conversation),
                cliSessionId is null ? EarlierTranscript(request.Conversation) : null,
                cliSessionId,
                cliSessionId is null ? request.SystemMessage : null,
                onUpdate,
                cancellationToken).ConfigureAwait(false);

            using var state = JsonDocument.Parse($"{{\"grokSessionId\":\"{JsonEncodedText.Encode(segment.SessionId)}\"}}");
            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, segment.Parts),
                AssistantPartContentIds = segment.ContentIds,
                ProviderSessionId = segment.SessionId,
                ProviderState = state.RootElement.Clone(),
            };
        }

        private GrokAcpSession SessionFor(string sessionId)
            => _sessions.GetOrAdd(sessionId, static _ => new GrokAcpSession());

        private static string LastUserText(IReadOnlyList<AgentConversationMessage> conversation)
        {
            for (var index = conversation.Count - 1; index >= 0; index--)
            {
                if (conversation[index].Role != AgentConversationRole.User)
                {
                    continue;
                }

                var text = string.Concat(conversation[index].Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));
                if (text.Length > 0)
                {
                    return text;
                }
            }

            throw new InvalidOperationException("Grok ACP needs a user message.");
        }

        private static string? BlankToNull(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>
        /// The turns before the prompt that is about to be sent, for a CLI session that does not already have them.
        /// </summary>
        private static string? EarlierTranscript(IReadOnlyList<AgentConversationMessage> conversation)
        {
            var end = conversation.Count;
            while (end > 0 && conversation[end - 1].Role == AgentConversationRole.User)
            {
                end--;
            }

            if (end == 0)
            {
                return null;
            }

            var builder = new StringBuilder();
            var wrote = false;
            for (var index = 0; index < end; index++)
            {
                var message = conversation[index];
                foreach (var part in message.Parts)
                {
                    if (part is not AgentMessagePart.Text text || string.IsNullOrWhiteSpace(text.Value))
                    {
                        continue;
                    }

                    if (!wrote)
                    {
                        builder.AppendLine("The conversation below is this CodeAlta session so far. It is context for the request that follows, not a new request.");
                        builder.AppendLine();
                        wrote = true;
                    }

                    builder.Append(message.Role switch
                    {
                        AgentConversationRole.Assistant => "Assistant: ",
                        AgentConversationRole.System => "System: ",
                        AgentConversationRole.Tool => "Tool: ",
                        _ => "User: ",
                    });
                    builder.AppendLine(text.Value.Trim());
                    builder.AppendLine();
                }
            }

            if (!wrote)
            {
                return null;
            }

            var transcript = builder.ToString().TrimEnd();
            const int limit = 24_000;
            if (transcript.Length > limit)
            {
                transcript = "(earlier turns omitted)\n" + transcript[^limit..];
            }

            return transcript;
        }
    }
}
