using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Grok;

/// <summary>
/// Browser and device sign-in through the Grok Build CLI, and the model list that CLI prints.
/// </summary>
public static class GrokAgentLogin
{
    private static readonly Regex UrlPattern = new(@"https://[^\s<>""']+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UserCodePattern = new(@"user\s*code\s*[:=]\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly AgentReasoningEffort[] Efforts =
    [
        AgentReasoningEffort.Low,
        AgentReasoningEffort.Medium,
        AgentReasoningEffort.High,
        AgentReasoningEffort.XHigh,
    ];

    /// <summary>
    /// Runs <c>grok login</c>. Device mode appends <c>--device-auth</c> and does not open a browser.
    /// </summary>
    /// <param name="launch">The CLI launch. Its arguments are placed before <c>login</c>.</param>
    /// <param name="deviceFlow"><see langword="true"/> for <c>grok login --device-auth</c>.</param>
    /// <param name="onPrompt">Receives the https URL and, when the CLI prints one, the user code.</param>
    /// <param name="cancellationToken">Cancels the login and the prompt.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">The CLI prints no https URL or exits with a failure.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static async Task LoginAsync(GrokCliLaunch launch, bool deviceFlow, Func<Uri, string?, CancellationToken, ValueTask> onPrompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(onPrompt);
        var arguments = new List<string>(launch.Arguments) { "login" };
        if (deviceFlow)
        {
            arguments.Add("--device-auth");
        }

        using var process = GrokProcess.Start(new GrokCliLaunch(launch.FileName, arguments, launch.WorkingDirectory));
        var output = new StringBuilder();
        var gate = new object();
        var prompt = new TaskCompletionSource<(Uri Url, string? UserCode)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? delay = null;

        void Consider(bool allowMissingCode)
        {
            string text;
            lock (gate)
            {
                text = output.ToString();
            }

            var url = FindUrl(text);
            if (url is null)
            {
                return;
            }

            var code = FindUserCode(text);
            if (deviceFlow && code is null && !allowMissingCode)
            {
                delay ??= DelayPromptAsync();
                return;
            }

            prompt.TrySetResult((url, deviceFlow ? code : null));
        }

        async Task DelayPromptAsync()
        {
            try
            {
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Consider(allowMissingCode: true);
        }

        async Task PumpAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lock (gate)
                {
                    output.AppendLine(line);
                }

                Consider(allowMissingCode: false);
            }
        }

        try
        {
            var pumps = Task.WhenAll(PumpAsync(process.StandardOutput), PumpAsync(process.StandardError));
            var exited = process.WaitForExitAsync(cancellationToken);
            var first = await Task.WhenAny(prompt.Task, exited).ConfigureAwait(false);
            if (first != prompt.Task)
            {
                await pumps.ConfigureAwait(false);
                Consider(allowMissingCode: true);
            }

            if (!prompt.Task.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("Grok login did not print an https URL. " + Tail(output));
            }

            var shown = await prompt.Task.ConfigureAwait(false);
            await onPrompt(shown.Url, shown.UserCode, cancellationToken).ConfigureAwait(false);
            await exited.ConfigureAwait(false);
            await pumps.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Grok login exited with code {process.ExitCode}. {Tail(output)}");
            }
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            throw;
        }
        finally
        {
            Stop(process);
        }
    }

    /// <summary>
    /// Runs <c>grok models</c> and reports whether that command shows a signed-in CLI.
    /// </summary>
    /// <param name="launch">The CLI launch. Its arguments are placed before <c>models</c>.</param>
    /// <param name="cancellationToken">Cancels the process.</param>
    /// <returns><see langword="true"/> when the CLI lists models or says it is signed in.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="launch"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static async Task<bool> IsAuthenticatedAsync(GrokCliLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        using var process = GrokProcess.Start(ModelsLaunch(launch));
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var text = output + await stderr.ConfigureAwait(false);
            if (IsSignedOutText(text))
            {
                return false;
            }

            return ParseModels(output).Count > 0 || IsSignedInText(text);
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            throw;
        }
        finally
        {
            Stop(process);
        }
    }

    /// <summary>
    /// Interprets CLI text. Negative phrases win, so "not logged in" is signed out.
    /// </summary>
    /// <param name="text">Combined command output.</param>
    /// <returns><see langword="true"/> when the text reports a login and no signed-out phrase.</returns>
    public static bool IsSignedInText(string? text)
        => !IsSignedOutText(text) && Contains(text, "logged in", "authenticated", "signed in");

    /// <summary>
    /// Starts <c>grok agent stdio</c>, reads the model catalog from the <c>initialize</c> result, and stops the process.
    /// </summary>
    /// <param name="launch">The CLI launch. Its arguments are placed before <c>--no-auto-update</c>.</param>
    /// <param name="cancellationToken">Cancels the process.</param>
    /// <returns>The agent models, in the order the CLI reported them, each with the efforts that model accepts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="launch"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The CLI exits without reporting a model.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(GrokCliLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var arguments = AgentArguments(launch.Arguments, modelId: null, effort: null);
        using var process = GrokProcess.Start(new GrokCliLaunch(launch.FileName, arguments, launch.WorkingDirectory));
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(InitializeRequest.AsMemory(), cancellationToken).ConfigureAwait(false);
            string? line = null;
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } next)
            {
                if (!string.IsNullOrWhiteSpace(next) && next.TrimStart().StartsWith('{'))
                {
                    line = next;
                    break;
                }
            }

            if (line is null)
            {
                throw new InvalidOperationException(await FailureAsync(stderr, "The Grok Build CLI did not list models.").ConfigureAwait(false));
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("The Grok Build CLI did not list models.", ex);
            }

            using (document)
            {
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException(ErrorMessage(error));
                }

                var models = ParseAgentCatalog(document.RootElement);
                if (models.Count == 0)
                {
                    throw new InvalidOperationException("The Grok Build CLI did not list models.");
                }

                return models;
            }
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            throw;
        }
        finally
        {
            Stop(process);
        }
    }

    /// <summary>
    /// Reads <c>grok models</c> text. A model line is <c>id - display name</c>, or a bullet
    /// <c>- id</c> / <c>* id (default)</c>. Banner lines are skipped. This is the signed-in check.
    /// The agent catalog, including each model's efforts, comes from <see cref="ParseAgentCatalog"/>.
    /// </summary>
    /// <param name="text">The command output.</param>
    /// <returns>The models, in the order the CLI printed them.</returns>
    public static IReadOnlyList<AgentModelInfo> ParseModels(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var models = new List<AgentModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseModelLine(raw.Trim(), out var id, out var name) || !seen.Add(id))
            {
                continue;
            }

            models.Add(TextModel(id, name));
        }

        return models;
    }

    /// <summary>
    /// Reads the agent catalog from an <c>initialize</c> response. Models live at
    /// <c>result._meta.modelState.availableModels</c>. Each model keeps its <c>modelId</c>, the reasoning
    /// efforts that entry lists, and the limits and abilities its <c>_meta</c> reports: <c>totalContextTokens</c>
    /// is the context window, and <c>acceptsImages</c> and <c>toolCall</c> are image input and tool calling.
    /// </summary>
    /// <param name="initializeResponse">The JSON-RPC <c>initialize</c> response, or its <c>result</c> object.</param>
    /// <returns>The models, in the order the CLI reported them.</returns>
    public static IReadOnlyList<AgentModelInfo> ParseAgentCatalog(JsonElement initializeResponse)
    {
        var result = initializeResponse.ValueKind == JsonValueKind.Object && initializeResponse.TryGetProperty("result", out var nested)
            ? nested
            : initializeResponse;
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("_meta", out var meta)
            || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("modelState", out var state)
            || state.ValueKind != JsonValueKind.Object
            || !state.TryGetProperty("availableModels", out var available)
            || available.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<AgentModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in available.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object
                || !model.TryGetProperty("modelId", out var idProperty)
                || idProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idProperty.GetString()?.Trim();
            if (string.IsNullOrEmpty(id) || !seen.Add(id))
            {
                continue;
            }

            var name = model.TryGetProperty("name", out var nameProperty) && nameProperty.ValueKind == JsonValueKind.String
                ? nameProperty.GetString()?.Trim()
                : null;
            var description = model.TryGetProperty("description", out var descriptionProperty) && descriptionProperty.ValueKind == JsonValueKind.String
                ? descriptionProperty.GetString()?.Trim()
                : null;
            var efforts = ReadEfforts(model, out var defaultEffort);
            models.Add(new AgentModelInfo(
                id,
                string.IsNullOrEmpty(name) ? id : name,
                string.IsNullOrEmpty(description) ? null : description,
                Provider: GrokModelProviderRuntime.ProviderType,
                DefaultReasoningEffort: defaultEffort,
                SupportedReasoningEfforts: efforts,
                Capabilities: ReadCapabilities(model, efforts)));
        }

        return models;
    }

    /// <summary>
    /// The argument list of one ACP turn: configured arguments, then <c>--no-auto-update agent --always-approve</c>,
    /// the model and effort, then <c>stdio</c>. <c>--no-auto-update</c> is a top-level flag and comes before <c>agent</c>.
    /// </summary>
    /// <param name="configured">Arguments from the provider definition, placed before <c>agent</c>.</param>
    /// <param name="modelId">The model id, or empty to leave the CLI's model.</param>
    /// <param name="effort">The reasoning effort, or null to leave the CLI's effort.</param>
    /// <returns>The arguments that follow the executable.</returns>
    internal static IReadOnlyList<string> AgentArguments(IReadOnlyList<string>? configured, string? modelId, AgentReasoningEffort? effort)
    {
        var arguments = new List<string>();
        if (configured is not null)
        {
            arguments.AddRange(configured.Where(static argument => !string.IsNullOrWhiteSpace(argument)).Select(static argument => argument.Trim()));
        }

        arguments.Add("--no-auto-update");
        arguments.Add("agent");
        arguments.Add("--always-approve");
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            arguments.Add("--model");
            arguments.Add(modelId.Trim());
        }

        if (effort is { } selected && selected != AgentReasoningEffort.None)
        {
            arguments.Add("--effort");
            arguments.Add(EffortName(selected));
        }

        arguments.Add("stdio");
        return arguments;
    }

    internal static string EffortName(AgentReasoningEffort effort)
        => effort switch
        {
            AgentReasoningEffort.Minimal => "minimal",
            AgentReasoningEffort.Low => "low",
            AgentReasoningEffort.Medium => "medium",
            AgentReasoningEffort.High => "high",
            AgentReasoningEffort.XHigh => "xhigh",
            AgentReasoningEffort.Max => "max",
            _ => "high",
        };

    private const string InitializeRequest = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false},"clientInfo":{"name":"CodeAlta","version":"0"}}}""";

    private static GrokCliLaunch ModelsLaunch(GrokCliLaunch launch)
        => new(launch.FileName, [.. launch.Arguments, "models"], launch.WorkingDirectory);

    private static bool TryParseModelLine(string line, out string id, out string name)
    {
        id = string.Empty;
        name = string.Empty;
        var separator = line.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0 && line[0] is not ('-' or '*'))
        {
            id = line[..separator].Trim();
            name = line[(separator + 3)..].Trim();
            return id.Length > 0 && name.Length > 0 && !id.Contains(' ');
        }

        if (line.Length < 3 || line[0] is not ('-' or '*') || line[1] != ' ')
        {
            return false;
        }

        var body = line[2..].Trim();
        const string marker = " (default)";
        if (body.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
        {
            body = body[..^marker.Length].Trim();
        }

        if (body.Length == 0 || body.Contains(' '))
        {
            return false;
        }

        id = body;
        name = body;
        return true;
    }

    private static AgentModelInfo TextModel(string id, string name)
        => new(
            id,
            name,
            Provider: GrokModelProviderRuntime.ProviderType,
            DefaultReasoningEffort: AgentReasoningEffort.High,
            SupportedReasoningEfforts: Efforts,
            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [AgentReasoningEffortCapability.CompleteListKey] = true,
                ["supportsReasoning"] = true,
            });

    private static Dictionary<string, object?>? ReadCapabilities(JsonElement model, IReadOnlyList<AgentReasoningEffort>? efforts)
    {
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (efforts is not null)
        {
            capabilities[AgentReasoningEffortCapability.CompleteListKey] = true;
            capabilities["supportsReasoning"] = true;
        }

        if (model.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            SetLimit(capabilities, meta, "totalContextTokens", "contextWindow", "contextWindowTokens");
            SetLimit(capabilities, meta, "contextWindow", "contextWindow", "contextWindowTokens");
            SetLimit(capabilities, meta, "maxCompletionTokens", "outputTokenLimit", "maxTokens");
            SetLimit(capabilities, meta, "maxOutputTokens", "outputTokenLimit", "maxTokens");
            SetLimit(capabilities, meta, "inputTokenLimit", "inputTokenLimit", "maxInputTokens");
            SetLimit(capabilities, meta, "maxInputTokens", "inputTokenLimit", "maxInputTokens");
            SetFlag(capabilities, meta, "supportsToolCall", "supportsToolCall", "toolCall", "tool_call");
            SetFlag(capabilities, meta, "supportsImageInput", "acceptsImages", "supportsImageInput", "supportsImages");
            if (!capabilities.ContainsKey("inputModalities") &&
                meta.TryGetProperty("inputModalities", out var modalities) &&
                modalities.ValueKind == JsonValueKind.Array)
            {
                var values = new List<string>();
                foreach (var modality in modalities.EnumerateArray())
                {
                    if (modality.ValueKind == JsonValueKind.String && modality.GetString() is { Length: > 0 } text)
                    {
                        values.Add(text);
                    }
                }

                if (values.Count > 0)
                {
                    capabilities["inputModalities"] = values;
                }
            }
        }

        return capabilities.Count == 0 ? null : capabilities;
    }

    private static void SetLimit(Dictionary<string, object?> capabilities, JsonElement meta, string source, params string[] targets)
    {
        if (!meta.TryGetProperty(source, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out var value) ||
            value <= 0)
        {
            return;
        }

        foreach (var target in targets)
        {
            capabilities.TryAdd(target, value);
        }
    }

    private static void SetFlag(Dictionary<string, object?> capabilities, JsonElement meta, string target, params string[] sources)
    {
        if (capabilities.ContainsKey(target))
        {
            return;
        }

        foreach (var source in sources)
        {
            if (!meta.TryGetProperty(source, out var property))
            {
                continue;
            }

            if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                capabilities[target] = property.GetBoolean();
                return;
            }
        }
    }

    private static IReadOnlyList<AgentReasoningEffort>? ReadEfforts(JsonElement model, out AgentReasoningEffort? defaultEffort)
    {
        defaultEffort = null;
        if (!model.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (meta.TryGetProperty("supportsReasoningEffort", out var supports) && supports.ValueKind == JsonValueKind.False)
        {
            return null;
        }

        if (!meta.TryGetProperty("reasoningEfforts", out var listed) || listed.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var efforts = new List<AgentReasoningEffort>();
        foreach (var effort in listed.EnumerateArray())
        {
            var id = effort.ValueKind == JsonValueKind.String
                ? effort.GetString()
                : effort.ValueKind == JsonValueKind.Object && effort.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.String
                    ? idProperty.GetString()
                    : null;
            if (ParseEffort(id) is not { } parsed || efforts.Contains(parsed))
            {
                continue;
            }

            efforts.Add(parsed);
            if (defaultEffort is null
                && effort.ValueKind == JsonValueKind.Object
                && effort.TryGetProperty("default", out var isDefault)
                && isDefault.ValueKind == JsonValueKind.True)
            {
                defaultEffort = parsed;
            }
        }

        if (efforts.Count == 0)
        {
            return null;
        }

        if (defaultEffort is null
            && meta.TryGetProperty("reasoningEffort", out var current)
            && current.ValueKind == JsonValueKind.String
            && ParseEffort(current.GetString()) is { } selected
            && efforts.Contains(selected))
        {
            defaultEffort = selected;
        }

        defaultEffort ??= efforts.Contains(AgentReasoningEffort.High) ? AgentReasoningEffort.High : efforts[0];
        return efforts;
    }

    private static AgentReasoningEffort? ParseEffort(string? id)
        => id?.Trim().ToLowerInvariant() switch
        {
            "none" => AgentReasoningEffort.None,
            "minimal" => AgentReasoningEffort.Minimal,
            "low" => AgentReasoningEffort.Low,
            "medium" => AgentReasoningEffort.Medium,
            "high" => AgentReasoningEffort.High,
            "xhigh" => AgentReasoningEffort.XHigh,
            "max" => AgentReasoningEffort.Max,
            _ => null,
        };

    private static async Task<string> FailureAsync(Task<string> stderr, string fallback)
    {
        var error = (await stderr.ConfigureAwait(false)).Trim();
        return error.Length == 0 ? fallback : error;
    }

    private static string ErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(message.GetString()))
        {
            return message.GetString()!;
        }

        return "The Grok Build CLI did not list models.";
    }

    private static bool IsSignedOutText(string? text)
        => Contains(text, "not logged in", "not authenticated", "signed out", "login required");

    private static bool Contains(string? text, params string[] phrases)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var sample = text.ToLowerInvariant();
        foreach (var phrase in phrases)
        {
            if (sample.Contains(phrase, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Uri? FindUrl(string text)
    {
        var match = UrlPattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Value.TrimEnd('.', ',', ';', ')', ']');
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    private static string? FindUserCode(string text)
    {
        var match = UserCodePattern.Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string Tail(StringBuilder output)
    {
        var text = output.ToString().Trim();
        return text.Length == 0 ? "The CLI produced no output." : text;
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
