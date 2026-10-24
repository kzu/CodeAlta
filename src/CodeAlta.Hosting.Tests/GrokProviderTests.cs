using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Grok;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting.Tests;

[TestClass]
public sealed class GrokProviderTests
{
    private const string LoginUrl = "https://accounts.x.ai/device";
    private const string AssistantText = "Scripted grok assistant text.";

    [TestMethod]
    public void Registry_AcceptsGrokBuildAsItsOwnRuntime()
    {
        using var root = new TempRoot();
        var definition = Definition(root.ConfigPath);
        definition.ApiKey = null;

        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            definition, root.Path, null, out var descriptor, out var createRuntime));
        Assert.AreEqual(GrokModelProviderRuntime.ProviderType, descriptor.ProviderType);
        Assert.AreNotEqual("xai", descriptor.ProviderType);
        Assert.AreNotEqual("claude-code", descriptor.ProviderType);
        Assert.AreNotEqual("cursor", descriptor.ProviderType);
        Assert.AreEqual("grok-4.7", descriptor.DefaultModelId);

        var runtime = createRuntime();
        Assert.IsInstanceOfType<GrokModelProviderRuntime>(runtime);
        Assert.AreEqual(AgentTransportKind.GrokBuildCli, ((GrokModelProviderRuntime)runtime).RuntimeDescriptor.TransportKind);
    }

    [TestMethod]
    public void Config_KeepsGrokBuildApartFromTheXaiAlias()
    {
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "config.toml"), """
            [providers.builder]
            type = "grok-cli"
            model = "grok-4.5"

            [providers.direct]
            type = "grok"
            """);
        var definitions = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root.Path }).LoadGlobalProviderDefinitions(includeDisabled: true);

        var builder = definitions.Single(definition => definition.ProviderKey == "builder");
        Assert.AreEqual(GrokModelProviderRuntime.ProviderType, builder.ProviderType);
        Assert.AreEqual("Grok Build", builder.DisplayName);
        Assert.IsNull(builder.ApiKey);
        Assert.AreEqual("xai", definitions.Single(definition => definition.ProviderKey == "direct").ProviderType);

        var rejected = CodeAltaConfigStore.ValidateGlobalConfigContent("""
            [providers.builder]
            type = "grok-build"
            api_key = "secret"
            """);
        Assert.IsFalse(rejected.IsValid);
    }

    [TestMethod]
    public void AuthMethod_PrefersTheApiKeyOnlyWhenItIsSet()
    {
        using var both = JsonDocument.Parse("""{"result":{"authMethods":[{"id":"cached_token"},{"id":"xai.api_key"}]}}""");
        Assert.AreEqual(GrokAuthMethods.CachedToken, GrokAuthMethods.Select(both.RootElement, hasApiKey: false));
        Assert.AreEqual(GrokAuthMethods.ApiKey, GrokAuthMethods.Select(both.RootElement, hasApiKey: true));

        using var token = JsonDocument.Parse("""{"result":{"authMethods":[{"id":"cached_token"}]}}""");
        Assert.AreEqual(GrokAuthMethods.CachedToken, GrokAuthMethods.Select(token.RootElement, hasApiKey: true));

        using var none = JsonDocument.Parse("""{"result":{"authMethods":[]}}""");
        Assert.IsNull(GrokAuthMethods.Select(none.RootElement, hasApiKey: false));
    }

    [TestMethod]
    public void ParseAgentCatalog_KeepsTheWindowToolsAndImageInputTheCliReports()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "result": {
                "_meta": {
                  "modelState": {
                    "availableModels": [
                      {
                        "modelId": "grok-4.7",
                        "name": "Grok 4.7",
                        "_meta": {
                          "totalContextTokens": 500000,
                          "maxCompletionTokens": 128000,
                          "supportsReasoningEffort": true,
                          "reasoningEfforts": ["low", "high"],
                          "acceptsImages": true,
                          "toolCall": true,
                          "inputModalities": ["text", "image"]
                        }
                      },
                      {
                        "modelId": "grok-code",
                        "name": "Grok Code",
                        "_meta": { "supportsReasoningEffort": false, "acceptsImages": false, "toolCall": false }
                      }
                    ]
                  }
                }
              }
            }
            """);

        var models = GrokAgentLogin.ParseAgentCatalog(document.RootElement);

        var grok = models.Single(model => model.Id == "grok-4.7");
        Assert.AreEqual(500_000L, grok.Capabilities!["contextWindow"]);
        Assert.AreEqual(500_000L, grok.Capabilities["contextWindowTokens"]);
        Assert.AreEqual(128_000L, grok.Capabilities["outputTokenLimit"]);
        Assert.AreEqual(true, grok.Capabilities["supportsToolCall"]);
        Assert.AreEqual(true, grok.Capabilities["supportsImageInput"]);
        Assert.AreEqual(true, AgentImageInputCapability.Read(grok));

        var code = models.Single(model => model.Id == "grok-code");
        Assert.AreEqual(false, code.Capabilities!["supportsToolCall"]);
        Assert.AreEqual(false, code.Capabilities["supportsImageInput"]);
        Assert.IsFalse(code.Capabilities.ContainsKey("contextWindow"));
    }

    [TestMethod]
    public void ParseModels_KeepsThePrintedIdAndOffersEffort()
    {
        var models = GrokAgentLogin.ParseModels("""
            grok-4.5 - Grok 4.5 (current)
            Available models
            grok-4 - Grok 4
            """);

        Assert.AreEqual(2, models.Count);
        Assert.AreEqual("grok-4.5", models[0].Id);
        Assert.AreEqual("Grok 4.5 (current)", models[0].DisplayName);
        Assert.AreEqual(AgentReasoningEffort.High, models[0].DefaultReasoningEffort);
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High, AgentReasoningEffort.XHigh },
            models[0].SupportedReasoningEfforts!.ToArray());
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
        Assert.IsFalse(root.Log().Contains("device-auth", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DeviceLogin_SurfacesTheUrlAndUserCode()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: true, userCode: "ABCD-1234");
        var prompts = new List<ProviderLoginPrompt>();

        var status = await ConfiguredProviderLogin.LoginAsync(Definition(root.ConfigPath), root.Path, "device",
            (prompt, _) => { prompts.Add(prompt); return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.AreEqual(LoginUrl, prompts.Single().Uri.AbsoluteUri.TrimEnd('/'));
        Assert.AreEqual("ABCD-1234", prompts[0].UserCode);
        Assert.IsTrue(status.SignedIn);
        StringAssert.Contains(root.Log(), "device-auth");
        Assert.IsFalse(root.Log().Contains("browser-login", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AlreadyAuthenticatedCli_IsSignedInWithoutAnApiKey()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        var definition = Definition(root.ConfigPath);
        Assert.IsNull(definition.ApiKey);

        var status = await ConfiguredProviderLogin.GetStatusAsync(definition, root.Path, CancellationToken.None);

        Assert.IsTrue(status.SignedIn);
        Assert.IsTrue(status.Usable);
        Assert.AreEqual("Grok Build", status.Detail);
        StringAssert.Contains(root.Log(), "models");
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
        Assert.AreEqual(1, root.Log().Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.Trim() == "models"));

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
        var runtime = (GrokModelProviderRuntime)createRuntime();
        string? streamed = null;

        var response = await runtime.CreateTurnExecutor().ExecuteTurnAsync(Request(runtime, descriptor), (delta, _) =>
        {
            streamed = delta.Text;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        var text = string.Concat(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Select(part => part.Value));
        Assert.AreEqual(AssistantText, text);
        Assert.AreEqual(AssistantText, streamed);
        Assert.AreEqual("grok-session", response.ProviderSessionId);
        var log = root.Log();
        var method = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY")) ? "cached_token" : "xai.api_key";
        StringAssert.Contains(log, "initialize");
        StringAssert.Contains(log, "authenticate " + method);
        StringAssert.Contains(log, "session/new");
        StringAssert.Contains(log, "model grok-4.5");
        StringAssert.Contains(log, "session/prompt");
        StringAssert.Contains(log, "prompt-text Say one sentence.");
        StringAssert.Contains(log, "permission");
        StringAssert.Contains(log, "allow-once");
        StringAssert.Contains(log, "selected");
    }

    [TestMethod]
    public async Task AcpTurn_IgnoresAResponseWhoseIdIsNotANumber()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null, assistant: AssistantText, traces: new object[]
        {
            new Dictionary<string, object?> { ["kind"] = "response", ["id"] = "skills-reload" },
            new Dictionary<string, object?> { ["kind"] = "message", ["text"] = AssistantText },
        });
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out var descriptor, out var createRuntime));
        var runtime = (GrokModelProviderRuntime)createRuntime();

        var response = await runtime.CreateTurnExecutor().ExecuteTurnAsync(Request(runtime, descriptor), static (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        var text = string.Concat(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Select(part => part.Value));
        Assert.AreEqual(AssistantText, text);
    }

    [TestMethod]
    public async Task AcpTurn_PassesTheSelectedEffort()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out var descriptor, out var createRuntime));
        var runtime = (GrokModelProviderRuntime)createRuntime();
        var request = Request(runtime, descriptor) with { ReasoningEffort = AgentReasoningEffort.Low };

        await runtime.CreateTurnExecutor().ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        StringAssert.Contains(root.Log(), "effort low");
        StringAssert.Contains(root.Log(), "model grok-4.5");
    }

    [TestMethod]
    public async Task AcpTurn_ReportsToolsAsCallsTheSessionRuns()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null, assistant: AssistantText, traces: new object[]
        {
            new Dictionary<string, object?> { ["kind"] = "thought", ["text"] = "Checking the listing." },
            new Dictionary<string, object?> { ["kind"] = "tool", ["id"] = "read-models", ["title"] = "Read models", ["toolKind"] = "read", ["status"] = "pending", ["path"] = "src/Program.cs" },
            new Dictionary<string, object?> { ["kind"] = "tool", ["id"] = "read-models", ["update"] = true, ["status"] = "completed", ["output"] = "grok-4.5" },
            new Dictionary<string, object?> { ["kind"] = "message", ["text"] = AssistantText },
        });
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out var descriptor, out var createRuntime));
        await using var runtime = (GrokModelProviderRuntime)createRuntime();
        var executor = runtime.CreateTurnExecutor();
        var host = (IAgentProviderToolHost)executor;
        var request = Request(runtime, descriptor);

        var response = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        var call = response.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("Read", call.Name);
        Assert.AreEqual("read-models", call.CallId);
        Assert.AreEqual("src/Program.cs", call.Arguments.GetProperty("file_path").GetString());
        Assert.IsFalse(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Any(part => part.Value == AssistantText));

        var definition = host.ResolveTool("session-grok", call, null);
        Assert.IsNotNull(definition);
        var started = host.WhenToolStarts("session-grok", call);
        Assert.IsNotNull(started);
        Assert.IsTrue(started.IsCompletedSuccessfully);
        var result = await definition.Handler(new AgentToolInvocation(
            new ModelProviderId("grok-build"), "session-grok", call.CallId, call.Name, call.Arguments), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(result.Success);

        response = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        var answer = Assert.IsInstanceOfType<AgentMessagePart.Text>(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single());
        Assert.AreEqual(AssistantText, answer.Value);
        Assert.AreEqual(1, root.Log().Split('\n').Count(static line => line.StartsWith("session/prompt", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AcpTurn_SendsTheSystemPromptOnlyWhenTheSessionStarts()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out var descriptor, out var createRuntime));
        var runtime = (GrokModelProviderRuntime)createRuntime();

        await runtime.CreateTurnExecutor().ExecuteTurnAsync(Request(runtime, descriptor, "Stay in the repository."), static (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        StringAssert.Contains(root.Log(), "prompt-resource codealta://system-prompt Stay in the repository.");

        var continued = Request(runtime, descriptor, "Stay in the repository") with
        {
            CanUseProviderContinuation = true,
            State = Request(runtime, descriptor).State with { ProviderSessionId = "grok-session-kept" },
        };
        var response = await runtime.CreateTurnExecutor().ExecuteTurnAsync(continued, static (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.AreEqual("grok-session-kept", response.ProviderSessionId);
        var log = root.Log();
        StringAssert.Contains(log, "session/load");
        Assert.AreEqual(1, log.Split('\n').Count(static line => line.Contains("prompt-resource", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AcpTurn_LoadsTheStoredCliSessionWhenTheCodeAltaSessionIsAttachedAgain()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out var descriptor, out var createRuntime));
        var runtime = (GrokModelProviderRuntime)createRuntime();
        var basis = Request(runtime, descriptor, "Stay in the repository.");
        var resumed = basis with
        {
            CanUseProviderContinuation = false,
            Conversation =
            [
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("drop the five markdown edits")]),
                new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("I will revert those five files.")]),
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("yes, do that")]),
            ],
            State = basis.State with { ProviderSessionId = "grok-session-kept" },
        };

        var response = await runtime.CreateTurnExecutor().ExecuteTurnAsync(resumed, static (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        Assert.AreEqual("grok-session-kept", response.ProviderSessionId, "The follow-up stays in the CLI session that already has the proposal.");
        var log = root.Log();
        StringAssert.Contains(log, "session/load");
        Assert.IsFalse(log.Contains("session/new", StringComparison.Ordinal), log);
        StringAssert.Contains(log, "prompt-session grok-session-kept");
        StringAssert.Contains(log, "prompt-text yes, do that");
        Assert.IsFalse(log.Contains("drop the five markdown edits", StringComparison.Ordinal), "The CLI session already holds the earlier turns.");
        Assert.AreEqual(0, log.Split('\n').Count(static line => line.Contains("prompt-resource", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AcpTurn_AfterARewindStartsANewCliSessionAndTellsItTheKeptConversation()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out var descriptor, out var createRuntime));
        var runtime = (GrokModelProviderRuntime)createRuntime();
        var basis = Request(runtime, descriptor, "Stay in the repository.");
        var rewound = basis with
        {
            CanUseProviderContinuation = false,
            Conversation =
            [
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("drop the five markdown edits")]),
                new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("I will revert those five files.")]),
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("yes, do that")]),
            ],
            State = basis.State with
            {
                ProviderSessionId = "grok-session-cut",
                ProviderBranch = new AgentProviderBranch(AgentProviderBranchKind.Rewind, AtEnd: false),
            },
        };

        var response = await runtime.CreateTurnExecutor().ExecuteTurnAsync(rewound, static (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        Assert.AreEqual("grok-session", response.ProviderSessionId, "The cut CLI transcript still holds the turns the rewind removed.");
        var log = root.Log();
        StringAssert.Contains(log, "session/new");
        Assert.IsFalse(log.Contains("session/load", StringComparison.Ordinal), log);
        StringAssert.Contains(log, "drop the five markdown edits");
        StringAssert.Contains(log, "I will revert those five files.");
        StringAssert.Contains(log, "prompt-text yes, do that");
        StringAssert.Contains(log, "prompt-resource codealta://system-prompt Stay in the repository.");
    }

    [TestMethod]
    public async Task Probe_ReportsTheModelsTheCliPrints()
    {
        using var root = new TempRoot();
        WriteConfig(root, authenticated: true, device: false, userCode: null);
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            Definition(root.ConfigPath), root.Path, null, out _, out var createRuntime));
        var runtime = (GrokModelProviderRuntime)createRuntime();

        var probe = await runtime.ProbeAsync(CancellationToken.None);

        Assert.AreEqual(ModelProviderAvailability.Ready, probe.Availability);
        Assert.AreEqual("grok-4.5", probe.Models![0].Id);
        Assert.AreEqual("Grok 4.5", probe.Models[0].DisplayName);
        StringAssert.Contains(probe.StatusMessage, "signed in");
    }

    [TestMethod]
    public void Command_PrefersTheExecutableOverACmdShim()
    {
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "grok.cmd"), "@echo off\r\n");
        File.WriteAllText(Path.Combine(root.Path, "grok.exe"), "");

        var resolved = GrokProcess.ResolveFile("grok", root.Path, windows: true);

        Assert.AreEqual(Path.Combine(root.Path, "grok.exe"), resolved);
    }

    private static CodeAltaProviderDocument Definition(string configPath)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "CodeAlta.Tests.GrokCli.dll");
        Assert.IsTrue(File.Exists(dll), dll);
        return new CodeAltaProviderDocument
        {
            ProviderKey = "grok-build",
            ProviderType = GrokModelProviderRuntime.ProviderType,
            Enabled = true,
            Command = "dotnet",
            Arguments = ["exec", dll, "--config", configPath],
        };
    }

    private static AgentTurnRequest Request(GrokModelProviderRuntime runtime, ModelProviderDescriptor descriptor, string? systemMessage = null)
        => new()
        {
            Provider = runtime.RuntimeDescriptor,
            ProviderId = descriptor.ProviderId,
            SessionId = "session-grok",
            RunId = new AgentRunId("run-grok"),
            ModelId = "grok-4.5",
            SystemMessage = systemMessage,
            Conversation = [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Say one sentence.")])],
            Tools = [],
            State = new AgentSessionState
            {
                SessionId = "session-grok",
                ProtocolFamily = GrokModelProviderRuntime.ProtocolFamily,
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
            ["models"] = new[] { "grok-4.5 - Grok 4.5", "Available models" },
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
            Path = Directory.CreateTempSubdirectory("codealta-grok-").FullName;
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
