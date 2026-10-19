using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Cursor;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting.Tests;

[TestClass]
public sealed class CursorProviderTests
{
    private const string LoginUrl = "https://cursor.example.test/login";
    private const string AssistantText = "Scripted cursor assistant text.";

    [TestMethod]
    public void Registry_AcceptsCursorAsItsOwnRuntime()
    {
        using var root = new TempRoot();
        var definition = Definition(root.ConfigPath);
        definition.ApiKey = null;

        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            definition, root.Path, null, out var descriptor, out var createRuntime));
        Assert.AreEqual(CursorModelProviderRuntime.ProviderType, descriptor.ProviderType);
        Assert.AreNotEqual("xai", descriptor.ProviderType);
        Assert.AreNotEqual("claude-code", descriptor.ProviderType);
        Assert.AreNotEqual("openai-chat", descriptor.ProviderType);
        Assert.IsNotNull(createRuntime);

        var runtime = createRuntime();
        Assert.IsInstanceOfType<CursorModelProviderRuntime>(runtime);
        Assert.AreEqual(AgentTransportKind.CursorAgentCli, ((CursorModelProviderRuntime)runtime).RuntimeDescriptor.TransportKind);
    }

    [TestMethod]
    public async Task BrowserLogin_SurfacesTheHttpsUrl()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        var prompts = new List<ProviderLoginPrompt>();

        var status = await ConfiguredProviderLogin.LoginAsync(Definition(root.ConfigPath), root.Path, "browser",
            (prompt, _) => { prompts.Add(prompt); return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.AreEqual(LoginUrl, prompts.Single().Uri.AbsoluteUri.TrimEnd('/'));
        Assert.IsNull(prompts[0].UserCode);
        Assert.IsTrue(status.SignedIn);
        Assert.IsTrue(status.Usable);
        StringAssert.Contains(root.Log(), "browser-login");
        Assert.IsFalse(root.Log().Contains("device-no-browser", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DeviceLogin_SurfacesTheUrlAndUserCodeWithoutOpeningABrowser()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: true, userCode: "ABCD-1234");
        var prompts = new List<ProviderLoginPrompt>();

        var status = await ConfiguredProviderLogin.LoginAsync(Definition(root.ConfigPath), root.Path, "device",
            (prompt, _) => { prompts.Add(prompt); return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.AreEqual(LoginUrl, prompts.Single().Uri.AbsoluteUri.TrimEnd('/'));
        Assert.AreEqual("ABCD-1234", prompts[0].UserCode);
        Assert.IsTrue(status.SignedIn);
        StringAssert.Contains(root.Log(), "device-no-browser");
        Assert.IsFalse(root.Log().Contains("browser-login", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AlreadyAuthenticatedCli_IsSignedInWithoutAnApiKeyOrLogin()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        var definition = Definition(root.ConfigPath);
        Assert.IsNull(definition.ApiKey);

        var status = await ConfiguredProviderLogin.GetStatusAsync(definition, root.Path, CancellationToken.None);

        Assert.IsTrue(status.SignedIn);
        Assert.IsTrue(status.Usable);
        Assert.AreEqual("status", root.Log().Trim());
    }

    [TestMethod]
    public async Task SignOut_ClearsTheSignedInViewUntilLogin()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        var definition = Definition(root.ConfigPath);

        Assert.IsTrue(await ConfiguredProviderLogin.SignOutAsync(definition, root.Path, CancellationToken.None));
        var signedOut = await ConfiguredProviderLogin.GetStatusAsync(definition, root.Path, CancellationToken.None);
        Assert.IsFalse(signedOut.SignedIn);
        Assert.IsFalse(root.Log().Contains("logout", StringComparison.Ordinal));
        Assert.AreEqual(1, root.Log().Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.Trim() == "status"));

        await ConfiguredProviderLogin.LoginAsync(definition, root.Path, "browser", static (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        var signedIn = await ConfiguredProviderLogin.GetStatusAsync(definition, root.Path, CancellationToken.None);
        Assert.IsTrue(signedIn.SignedIn);
    }

    [TestMethod]
    public async Task AcpTurn_ReturnsTheStreamedAssistantTextAndAnswersPermission()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null, assistant: AssistantText);
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out var descriptor, out var createRuntime));
        var runtime = (CursorModelProviderRuntime)createRuntime();
        string? streamed = null;

        var response = await runtime.CreateTurnExecutor().ExecuteTurnAsync(new AgentTurnRequest
        {
            Provider = runtime.RuntimeDescriptor,
            ProviderId = descriptor.ProviderId,
            SessionId = "session-cursor",
            RunId = new AgentRunId("run-cursor"),
            ModelId = "composer-2.5",
            Conversation = [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Say one sentence.")])],
            Tools = [],
            State = new AgentSessionState
            {
                SessionId = "session-cursor",
                ProtocolFamily = CursorModelProviderRuntime.ProtocolFamily,
                ProviderKey = descriptor.ProviderId.Value,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        }, (delta, _) =>
        {
            streamed = delta.Text;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        var text = string.Concat(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Select(part => part.Value));
        Assert.AreEqual(AssistantText, text);
        Assert.AreEqual(AssistantText, streamed);
        var log = root.Log();
        StringAssert.Contains(log, "initialize");
        StringAssert.Contains(log, "authenticate cursor_login");
        StringAssert.Contains(log, "session/new");
        StringAssert.Contains(log, "model composer-2.5");
        StringAssert.Contains(log, "session/prompt");
        StringAssert.Contains(log, "prompt-text Say one sentence.");
        Assert.IsFalse(log.Contains("prompt-resource", StringComparison.Ordinal));
        StringAssert.Contains(log, "permission");
        StringAssert.Contains(log, "allow-once");
        StringAssert.Contains(log, "selected");
        Assert.IsNull(response.ProviderActivities);
    }

    [TestMethod]
    public async Task AcpTurn_ReportsToolsAsCallsTheSessionRuns()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null, assistant: AssistantText, traces: new object[]
        {
            new Dictionary<string, object?> { ["kind"] = "thought", ["text"] = "Checking the listing. " },
            new Dictionary<string, object?> { ["kind"] = "thought", ["text"] = "The ids are suffixed." },
            new Dictionary<string, object?> { ["kind"] = "tool", ["id"] = "read-models", ["title"] = "Read models", ["toolKind"] = "read", ["status"] = "pending", ["path"] = "src/CursorModelEfforts.cs" },
            new Dictionary<string, object?> { ["kind"] = "tool", ["id"] = "read-models", ["update"] = true, ["status"] = "completed", ["output"] = "grok-4.7-high" },
            new Dictionary<string, object?> { ["kind"] = "tool", ["id"] = "shell-1", ["title"] = "Shell", ["toolKind"] = "execute", ["status"] = "completed", ["command"] = "agent models" },
            new Dictionary<string, object?> { ["kind"] = "thought", ["text"] = "Now I can answer." },
            new Dictionary<string, object?> { ["kind"] = "message", ["text"] = AssistantText },
        });
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out var descriptor, out var createRuntime));
        await using var runtime = (CursorModelProviderRuntime)createRuntime();
        var executor = runtime.CreateTurnExecutor();
        var host = (IAgentProviderToolHost)executor;
        var request = Request(runtime, descriptor);
        var deltas = new List<AgentTurnDelta>();

        var response = await executor.ExecuteTurnAsync(request, (delta, _) =>
        {
            deltas.Add(delta);
            return ValueTask.CompletedTask;
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        // The answer that follows the tools is a later segment, so it is not streamed as part of the message that calls them.
        Assert.IsTrue(deltas.All(static delta => delta.Kind == AgentContentKind.Reasoning));
        Assert.AreEqual("cursor-reasoning", deltas[0].ContentId);
        Assert.AreEqual("Checking the listing. ", deltas[0].Text);
        Assert.AreEqual("The ids are suffixed.", deltas[1].Text);
        Assert.IsFalse(deltas.Any(static delta => delta.Text.Contains("Now I can answer", StringComparison.Ordinal) || delta.Text == AssistantText));
        var firstThought = Assert.IsInstanceOfType<AgentMessagePart.Reasoning>(response.AssistantMessage.Parts[0]);
        Assert.AreEqual("Checking the listing. The ids are suffixed.", firstThought.Value);
        Assert.AreEqual("cursor-reasoning", response.AssistantPartContentIds![0]);
        Assert.IsNull(response.ProviderActivities);
        Assert.IsTrue(response.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().Any());

        var calls = new List<AgentMessagePart.ToolCall>();
        var results = new Dictionary<string, AgentToolResult>(StringComparer.Ordinal);
        var progress = new Dictionary<string, string>(StringComparer.Ordinal);
        var later = new List<AgentTurnDelta>();
        while (response.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().Any())
        {
            var running = new List<Task>();
            for (var index = 0; index < response.AssistantMessage.Parts.Count; index++)
            {
                if (response.AssistantMessage.Parts[index] is not AgentMessagePart.ToolCall call)
                {
                    continue;
                }

                calls.Add(call);
                Assert.IsNull(response.AssistantPartContentIds![index]);
                running.Add(RunCallAsync(host, call, results, progress));
            }

            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(15));
            response = await executor.ExecuteTurnAsync(request, (delta, _) =>
            {
                later.Add(delta);
                return ValueTask.CompletedTask;
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        }

        CollectionAssert.AreEqual(new[] { "Read", "Bash" }, calls.Select(static call => call.Name).ToArray());
        var read = calls[0];
        Assert.AreEqual("read-models", read.CallId);
        Assert.AreEqual("src/CursorModelEfforts.cs", read.Arguments.GetProperty("file_path").GetString());
        var readResult = results["read-models"];
        Assert.IsTrue(readResult.Success);
        var readText = readResult.Items.OfType<AgentToolResultItem.Text>().Single().Value;
        StringAssert.Matches(readText, new System.Text.RegularExpressions.Regex(@"^ *1: grok-4\.7-high$"));
        StringAssert.Contains(progress["read-models"], "grok-4.7-high");
        var shell = calls[1];
        Assert.AreEqual("shell-1", shell.CallId);
        Assert.AreEqual("agent models", shell.Arguments.GetProperty("command").GetString());
        Assert.IsTrue(results["shell-1"].Success);

        var secondThought = Assert.IsInstanceOfType<AgentMessagePart.Reasoning>(response.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().Single());
        Assert.AreEqual("Now I can answer.", secondThought.Value);
        var answer = Assert.IsInstanceOfType<AgentMessagePart.Text>(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single());
        Assert.AreEqual(AssistantText, answer.Value);
        Assert.IsTrue(later.Any(static delta => delta.Kind == AgentContentKind.Reasoning && delta.ContentId == "cursor-reasoning-2" && delta.Text == "Now I can answer."));
        Assert.IsTrue(later.Any(static delta => delta.Kind == AgentContentKind.Assistant && delta.ContentId == "cursor-assistant" && delta.Text == AssistantText));
        Assert.IsNull(response.ProviderActivities);
        Assert.AreEqual(1, root.Log().Split('\n').Count(static line => line.StartsWith("session/prompt", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AcpTurn_ReportsAnEditWithTheTextItReplaced()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null, assistant: AssistantText, traces: new object[]
        {
            new Dictionary<string, object?>
            {
                ["kind"] = "tool", ["id"] = "edit-1", ["title"] = "Edit Program", ["toolKind"] = "edit", ["status"] = "completed",
                ["path"] = "src/Program.cs", ["oldText"] = "return 1;", ["newText"] = "return 0;",
            },
            new Dictionary<string, object?> { ["kind"] = "message", ["text"] = AssistantText },
        });
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out var descriptor, out var createRuntime));
        await using var runtime = (CursorModelProviderRuntime)createRuntime();
        var executor = runtime.CreateTurnExecutor();
        var host = (IAgentProviderToolHost)executor;
        var request = Request(runtime, descriptor);

        var response = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        var edit = response.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("Edit", edit.Name);
        Assert.AreEqual("src/Program.cs", edit.Arguments.GetProperty("file_path").GetString());
        Assert.AreEqual("return 1;", edit.Arguments.GetProperty("old_string").GetString());
        Assert.AreEqual("return 0;", edit.Arguments.GetProperty("new_string").GetString());
        Assert.IsFalse(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Any(static part => part.Value == AssistantText));

        var definition = host.ResolveTool("session-cursor", edit, null);
        Assert.IsNotNull(definition);
        var result = await definition.Handler(new AgentToolInvocation(new ModelProviderId("cursor"), "session-cursor", edit.CallId, edit.Name, edit.Arguments), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(result.Success);

        response = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        var answer = Assert.IsInstanceOfType<AgentMessagePart.Text>(response.AssistantMessage.Parts.Single());
        Assert.AreEqual(AssistantText, answer.Value);
    }

    private static async Task RunCallAsync(IAgentProviderToolHost host, AgentMessagePart.ToolCall call, Dictionary<string, AgentToolResult> results, Dictionary<string, string> progress)
    {
        var definition = host.ResolveTool("session-cursor", call, null);
        Assert.IsNotNull(definition);
        var started = host.WhenToolStarts("session-cursor", call);
        Assert.IsNotNull(started);
        Assert.IsTrue(started.IsCompletedSuccessfully);
        var text = new StringBuilder();
        var result = await definition.Handler(new AgentToolInvocation(new ModelProviderId("cursor"), "session-cursor", call.CallId, call.Name, call.Arguments, (update, _) =>
        {
            text.Append(update.Delta);
            return ValueTask.CompletedTask;
        }), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        lock (results)
        {
            results[call.CallId] = result;
            progress[call.CallId] = text.ToString();
        }
    }

    [TestMethod]
    public async Task Probe_ReportsTheModelsTheCliPrints()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out _, out var createRuntime));
        var runtime = (CursorModelProviderRuntime)createRuntime();

        var probe = await runtime.ProbeAsync(CancellationToken.None);

        Assert.AreEqual(ModelProviderAvailability.Ready, probe.Availability);
        Assert.HasCount(2, probe.Models);
        Assert.AreEqual("auto", probe.Models![0].Id);
        Assert.AreEqual("Auto", probe.Models[0].DisplayName);
        Assert.AreEqual("composer-2.5", probe.Models[1].Id);
        Assert.AreEqual("Composer 2.5", probe.Models[1].DisplayName);
        StringAssert.Contains(root.Log(), "models");
    }

    [TestMethod]
    public void ParseModels_GroupsEffortVariantsAndKeepsFastAndThinkingApart()
    {
        var models = CursorAgentLogin.ParseModels("""
            auto - Auto (default)
            gpt-5.3-codex-low - Codex 5.3 Low
            gpt-5.3-codex-low-fast - Codex 5.3 Low Fast
            gpt-5.3-codex-high-fast - Codex 5.3 High Fast
            gpt-5.3-codex - Codex 5.3
            gpt-5.3-codex-fast - Codex 5.3 Fast
            gpt-5.3-codex-high - Codex 5.3 High
            gpt-5.3-codex-xhigh - Codex 5.3 Extra High
            composer-2.5 - Composer 2.5
            composer-2.5-fast - Composer 2.5 Fast
            claude-opus-5-5-low - Claude Opus 5.5 1M Low
            claude-opus-5-5-medium - Claude Opus 5.5 1M
            claude-opus-5-5-high - Claude Opus 5.5 1M High
            claude-opus-5-5-medium-fast - Claude Opus 5.5 1M Fast
            gpt-5.5-medium - GPT-5.5 1M
            gpt-5.5-extra-high - GPT-5.5 1M Extra High
            claude-opus-5-thinking-low - Claude Opus 5 1M Low Thinking
            claude-opus-5-thinking-high - Claude Opus 5 1M Thinking
            claude-opus-5-thinking-high-fast - Claude Opus 5 1M Thinking Fast
            claude-4.6-opus-high - Claude Opus 4.6 1M
            claude-4.6-opus-max - Claude Opus 4.6 1M Max
            claude-4.6-opus-high-thinking - Claude Opus 4.6 1M Thinking
            claude-4.6-opus-max-thinking - Claude Opus 4.6 1M Max Thinking
            claude-4.5-sonnet - Claude Sonnet 4.5
            claude-4.5-sonnet-thinking - Claude Sonnet 4.5 Thinking
            kimi-k3-low - Kimi K3 Low
            kimi-k3-high - Kimi K3 High
            kimi-k3-max - Kimi K3
            gpt-5.1-codex-max-low - GPT-5.1 Codex Max Low
            gpt-5.1-codex-max - GPT-5.1 Codex Max
            gpt-5.1-codex-max-high - GPT-5.1 Codex Max High
            claude-haiku-5-5-high - Claude Haiku 5.5  High No Thinking
            claude-haiku-5-5-thinking-high - Claude Haiku 5.5  High
            grok-4.7-low - Grok 4.7  Low
            grok-4.7-high - Grok 4.7  High
            grok-4.7-high-fast - Grok 4.7  High Fast​​
            widget-max - Widget
            """);

        CollectionAssert.AreEqual(
            new[] { "auto", "gpt-5.3-codex", "gpt-5.3-codex-fast", "composer-2.5", "composer-2.5-fast", "claude-opus-5-5", "claude-opus-5-5-fast", "gpt-5.5", "claude-opus-5-thinking", "claude-opus-5-thinking-fast", "claude-4.6-opus", "claude-4.6-opus-thinking", "claude-4.5-sonnet", "claude-4.5-sonnet-thinking", "kimi-k3", "gpt-5.1-codex-max", "claude-haiku-5-5", "claude-haiku-5-5-thinking", "grok-4.7", "grok-4.7-fast", "widget-max" },
            models.Select(model => model.Id).ToArray());

        var codex = models.Single(model => model.Id == "gpt-5.3-codex");
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High, AgentReasoningEffort.XHigh },
            codex.SupportedReasoningEfforts!.ToArray());
        Assert.AreEqual("Codex 5.3", codex.DisplayName);
        Assert.AreEqual(AgentReasoningEffort.Medium, codex.DefaultReasoningEffort);
        Assert.AreEqual("gpt-5.3-codex", CursorModelEfforts.Select(codex.Id, AgentReasoningEffort.Medium, codex));
        Assert.AreEqual("gpt-5.3-codex-xhigh", CursorModelEfforts.Select(codex.Id, AgentReasoningEffort.XHigh, codex));
        Assert.IsTrue(AgentReasoningEffortCapability.IsListComplete(codex));

        var codexFast = models.Single(model => model.Id == "gpt-5.3-codex-fast");
        Assert.AreEqual("Codex 5.3 Fast", codexFast.DisplayName);
        Assert.AreEqual("gpt-5.3-codex-high-fast", CursorModelEfforts.Select(codexFast.Id, AgentReasoningEffort.High, codexFast));

        var opus = models.Single(model => model.Id == "claude-opus-5-5");
        Assert.AreEqual("Claude Opus 5.5 1M", opus.DisplayName);
        Assert.AreEqual(AgentReasoningEffort.Medium, opus.DefaultReasoningEffort);
        Assert.AreEqual("claude-opus-5-5-low", CursorModelEfforts.Select(opus.Id, AgentReasoningEffort.Low, opus));

        var gpt = models.Single(model => model.Id == "gpt-5.5");
        Assert.AreEqual("gpt-5.5-extra-high", CursorModelEfforts.Select(gpt.Id, AgentReasoningEffort.XHigh, gpt));

        var thinking = models.Single(model => model.Id == "claude-opus-5-thinking");
        Assert.AreEqual("Claude Opus 5 1M Thinking", thinking.DisplayName);
        Assert.AreEqual("claude-opus-5-thinking-high", CursorModelEfforts.Select(thinking.Id, AgentReasoningEffort.High, thinking));
        Assert.AreEqual("claude-opus-5-thinking-high-fast", CursorModelEfforts.Select("claude-opus-5-thinking-fast", AgentReasoningEffort.High, models.Single(model => model.Id == "claude-opus-5-thinking-fast")));

        var opus46Thinking = models.Single(model => model.Id == "claude-4.6-opus-thinking");
        Assert.AreEqual("Claude Opus 4.6 1M Thinking", opus46Thinking.DisplayName);
        Assert.AreEqual("claude-4.6-opus-max-thinking", CursorModelEfforts.Select(opus46Thinking.Id, AgentReasoningEffort.Max, opus46Thinking));

        Assert.IsNull(models.Single(model => model.Id == "composer-2.5").SupportedReasoningEfforts);
        Assert.IsNull(models.Single(model => model.Id == "claude-4.5-sonnet-thinking").SupportedReasoningEfforts);
        Assert.AreEqual("Auto", models[0].DisplayName);

        var kimi = models.Single(model => model.Id == "kimi-k3");
        Assert.AreEqual("Kimi K3", kimi.DisplayName);
        Assert.AreEqual(AgentReasoningEffort.Max, kimi.DefaultReasoningEffort);
        Assert.AreEqual("kimi-k3-max", CursorModelEfforts.Select(kimi.Id, null, kimi));

        var codexMax = models.Single(model => model.Id == "gpt-5.1-codex-max");
        Assert.AreEqual("GPT-5.1 Codex Max", codexMax.DisplayName);
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High },
            codexMax.SupportedReasoningEfforts!.ToArray());
        Assert.AreEqual("gpt-5.1-codex-max", CursorModelEfforts.Select(codexMax.Id, AgentReasoningEffort.Medium, codexMax));

        Assert.AreEqual("Claude Haiku 5.5 No Thinking", models.Single(model => model.Id == "claude-haiku-5-5").DisplayName);
        Assert.AreEqual("Claude Haiku 5.5 Thinking", models.Single(model => model.Id == "claude-haiku-5-5-thinking").DisplayName);
        Assert.AreEqual("Grok 4.7", models.Single(model => model.Id == "grok-4.7").DisplayName);
        Assert.AreEqual("Grok 4.7 Fast", models.Single(model => model.Id == "grok-4.7-fast").DisplayName);
        Assert.AreEqual(AgentReasoningEffort.High, models.Single(model => model.Id == "grok-4.7").DefaultReasoningEffort);
        Assert.IsNull(models.Single(model => model.Id == "widget-max").SupportedReasoningEfforts);
        Assert.AreEqual("composer-2.5", CursorModelEfforts.Select("composer-2.5", AgentReasoningEffort.High, models.Single(model => model.Id == "composer-2.5")));

        Assert.IsTrue(AgentReasoningEffortCapability.TryMatchEffortSlug(models, "gpt-5.3-codex-xhigh", out var matched, out var matchedEffort));
        Assert.AreEqual("gpt-5.3-codex", matched.Id);
        Assert.AreEqual(AgentReasoningEffort.XHigh, matchedEffort);
        Assert.IsFalse(AgentReasoningEffortCapability.TryMatchEffortSlug(models, "composer-2.5", out _, out _));
        Assert.IsFalse(AgentReasoningEffortCapability.TryMatchEffortSlug(models, "not-a-model", out _, out _));
    }

    [TestMethod]
    public async Task AcpTurn_PassesTheCliIdOfTheSelectedEffort()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null, assistant: AssistantText);
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out var descriptor, out var createRuntime));
        var runtime = (CursorModelProviderRuntime)createRuntime();
        var model = CursorAgentLogin.ParseModels("gpt-5.4-low - GPT-5.4 1M Low\ngpt-5.4-high - GPT-5.4 1M High").Single();

        await runtime.CreateTurnExecutor().ExecuteTurnAsync(new AgentTurnRequest
        {
            Provider = runtime.RuntimeDescriptor,
            ProviderId = descriptor.ProviderId,
            SessionId = "session-cursor",
            RunId = new AgentRunId("run-cursor"),
            ModelId = model.Id,
            ModelInfo = model,
            ReasoningEffort = AgentReasoningEffort.Low,
            Conversation = [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Say one sentence.")])],
            Tools = [],
            State = new AgentSessionState
            {
                SessionId = "session-cursor",
                ProtocolFamily = CursorModelProviderRuntime.ProtocolFamily,
                ProviderKey = descriptor.ProviderId.Value,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        }, static (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        StringAssert.Contains(root.Log(), "model gpt-5.4-low");
    }

    [TestMethod]
    public async Task AcpTurn_SendsTheSystemPromptAsAdditionalContextWhenTheSessionStarts()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null, assistant: AssistantText);
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out var descriptor, out var createRuntime));
        var runtime = (CursorModelProviderRuntime)createRuntime();

        await runtime.CreateTurnExecutor().ExecuteTurnAsync(Request(runtime, descriptor, "Stay in the repository."), static (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        var log = root.Log();
        StringAssert.Contains(log, "session/new");
        StringAssert.Contains(log, "prompt-text Say one sentence.");
        StringAssert.Contains(log, "prompt-resource codealta://system-prompt Stay in the repository.");
    }

    [TestMethod]
    public async Task AcpTurn_LeavesTheSystemPromptOffAContinuedSession()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null, assistant: AssistantText);
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out var descriptor, out var createRuntime));
        var runtime = (CursorModelProviderRuntime)createRuntime();
        var request = Request(runtime, descriptor, "Stay in the repository.");
        request = request with
        {
            CanUseProviderContinuation = true,
            State = request.State with { ProviderSessionId = "cursor-session-kept" },
        };

        var response = await runtime.CreateTurnExecutor().ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        Assert.AreEqual("cursor-session-kept", response.ProviderSessionId);
        var log = root.Log();
        StringAssert.Contains(log, "session/load");
        StringAssert.Contains(log, "prompt-session cursor-session-kept");
        StringAssert.Contains(log, "prompt-text Say one sentence.");
        Assert.IsFalse(log.Contains("prompt-resource", StringComparison.Ordinal));
        Assert.IsFalse(log.Contains("Stay in the repository.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AgentCommand_PrefersTheCursorShimOverAnEarlierAgentExe()
    {
        using var root = new TempRoot();
        var other = Path.Combine(root.Path, "other");
        var cursor = Path.Combine(root.Path, "cursor");
        var older = Path.Combine(cursor, "versions", "2026.10.14-0f9edde");
        var newer = Path.Combine(cursor, "versions", "2026.10.18-ea085cb");
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);
        File.WriteAllText(Path.Combine(other, "agent.exe"), "");
        File.WriteAllText(Path.Combine(cursor, "agent.cmd"), "@echo off\r\nrem cursor-agent\r\n");
        File.WriteAllText(Path.Combine(older, "node.exe"), "");
        File.WriteAllText(Path.Combine(older, "index.js"), "");
        var node = Path.Combine(newer, "node.exe");
        var index = Path.Combine(newer, "index.js");
        File.WriteAllText(node, "");
        File.WriteAllText(index, "");

        var start = CursorProcess.Resolve(
            new CursorCliLaunch("agent", ["--extra"], null),
            "acp",
            other + Path.PathSeparator + cursor,
            windowsShims: true);

        Assert.AreEqual(node, start.FileName);
        Assert.AreEqual(index, start.Arguments[0]);
        Assert.AreEqual("--extra", start.Arguments[1]);
        Assert.AreEqual("acp", start.Arguments[2]);
        Assert.AreEqual("agent.cmd", start.InvokedAs);
    }

    private static CodeAltaProviderDocument Definition(string configPath)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "CodeAlta.Tests.CursorCli.dll");
        Assert.IsTrue(File.Exists(dll), dll);
        return new CodeAltaProviderDocument
        {
            ProviderKey = "cursor",
            ProviderType = CursorModelProviderRuntime.ProviderType,
            Enabled = true,
            Command = "dotnet",
            Arguments = ["exec", dll, "--config", configPath],
        };
    }

    private static AgentTurnRequest Request(CursorModelProviderRuntime runtime, ModelProviderDescriptor descriptor, string? systemMessage = null)
        => new()
        {
            Provider = runtime.RuntimeDescriptor,
            ProviderId = descriptor.ProviderId,
            SessionId = "session-cursor",
            RunId = new AgentRunId("run-cursor"),
            ModelId = "composer-2.5",
            SystemMessage = systemMessage,
            Conversation = [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Say one sentence.")])],
            Tools = [],
            State = new AgentSessionState
            {
                SessionId = "session-cursor",
                ProtocolFamily = CursorModelProviderRuntime.ProtocolFamily,
                ProviderKey = descriptor.ProviderId.Value,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };

    private static void WriteConfig(TempRoot root, bool authenticated, bool device, string? userCode, string? assistant = null, object? traces = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["authenticated"] = authenticated,
            ["device"] = device,
            ["url"] = LoginUrl,
            ["userCode"] = userCode,
            ["assistant"] = assistant ?? AssistantText,
            ["models"] = new[] { "auto - Auto", "Available models", "composer-2.5 - Composer 2.5", "Tip: use --model <id> to switch." },
            ["log"] = root.LogPath,
        };
        if (traces is not null)
        {
            payload["traces"] = traces;
        }

        File.WriteAllText(root.ConfigPath, JsonSerializer.Serialize(payload));
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = Directory.CreateTempSubdirectory("codealta-cursor-").FullName;
            ConfigPath = System.IO.Path.Combine(Path, "cli.json");
            LogPath = System.IO.Path.Combine(Path, "cli.log");
        }

        public string Path { get; }
        public string ConfigPath { get; }
        public string LogPath { get; }

        public string Log() => File.Exists(LogPath) ? File.ReadAllText(LogPath) : "";

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
