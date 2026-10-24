using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SubscriptionModelCapabilitiesTests
{
    [TestMethod]
    public async Task Describe_MapsCursorSlugsAndKeepsTheCliIdentity()
    {
        await using var catalog = Catalog();

        var opus = SubscriptionModelCapabilities.Describe(
            new AgentModelInfo("claude-4.6-opus", "Claude Opus 4.6 1M", Provider: "cursor"),
            catalog);
        Assert.AreEqual("claude-4.6-opus", opus.Id);
        Assert.AreEqual("Claude Opus 4.6 1M", opus.DisplayName);
        Assert.AreEqual(1_000_000L, opus.Capabilities!["contextWindow"]);
        Assert.AreEqual(128_000L, opus.Capabilities["outputTokenLimit"]);
        Assert.AreEqual(true, opus.Capabilities["supportsToolCall"]);
        Assert.AreEqual(true, AgentImageInputCapability.Read(opus));

        var thinking = SubscriptionModelCapabilities.Describe(
            new AgentModelInfo("claude-opus-5-thinking", "Claude Opus 5 1M Thinking", Provider: "cursor"),
            catalog);
        Assert.AreEqual("claude-opus-5-thinking", thinking.Id);
        Assert.AreEqual("Claude Opus 5 1M Thinking", thinking.DisplayName);
        Assert.AreEqual(1_000_000L, thinking.Capabilities!["contextWindow"]);

        var gpt = SubscriptionModelCapabilities.Describe(
            new AgentModelInfo("gpt-5.5", "GPT-5.5 1M", Provider: "cursor"),
            catalog);
        Assert.AreEqual(1_050_000L, gpt.Capabilities!["contextWindow"]);
        Assert.AreEqual(922_000L, gpt.Capabilities["inputTokenLimit"]);
        Assert.AreEqual(128_000L, gpt.Capabilities["outputTokenLimit"]);
        Assert.AreEqual(true, gpt.Capabilities["supportsToolCall"]);
        Assert.AreEqual(true, AgentImageInputCapability.Read(gpt));

        var composer = SubscriptionModelCapabilities.Describe(
            new AgentModelInfo("composer-2.5", "Composer 2.5", Provider: "cursor"),
            catalog);
        Assert.IsNull(composer.Capabilities);

        var auto = SubscriptionModelCapabilities.Describe(
            new AgentModelInfo("auto", "Auto", Provider: "cursor"),
            catalog);
        Assert.AreEqual("Auto", auto.DisplayName);
        Assert.IsNull(auto.Capabilities);
    }

    [TestMethod]
    public async Task Describe_UsesTheVendorsLimitsAndKeepsAWindowTheCliReported()
    {
        await using var catalog = Catalog();
        var reported = new AgentModelInfo(
            "grok-4.7",
            "Grok 4.7",
            Provider: "grok-build",
            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["contextWindow"] = 256_000L,
                ["contextWindowTokens"] = 256_000L,
            });

        var described = SubscriptionModelCapabilities.Describe(reported, catalog);

        Assert.AreEqual("grok-4.7", described.Id);
        Assert.AreEqual("Grok 4.7", described.DisplayName);
        Assert.AreEqual(256_000L, described.Capabilities!["contextWindow"]);
        Assert.AreEqual(372_000L, described.Capabilities["inputTokenLimit"]);
        Assert.AreEqual(128_000L, described.Capabilities["outputTokenLimit"]);
        Assert.AreEqual(true, described.Capabilities["supportsToolCall"]);
        Assert.AreEqual(true, AgentImageInputCapability.Read(described));
        Assert.AreEqual("xai", described.Capabilities["modelsDevProviderId"]);
    }

    private static ModelsDevCatalogService Catalog()
        => new(
            ModelsDevDatabaseJson.Deserialize(
                """
                {
                  "anthropic": {
                    "id": "anthropic",
                    "name": "Anthropic",
                    "models": {
                      "claude-opus-4-6": {
                        "id": "claude-opus-4-6",
                        "name": "Claude Opus 4.6",
                        "tool_call": true,
                        "attachment": true,
                        "modalities": { "input": ["text", "image"], "output": ["text"] },
                        "limit": { "context": 1000000, "output": 128000 }
                      },
                      "claude-opus-5": {
                        "id": "claude-opus-5",
                        "name": "Claude Opus 5",
                        "tool_call": true,
                        "modalities": { "input": ["text", "image"], "output": ["text"] },
                        "limit": { "context": 1000000, "output": 128000 }
                      }
                    }
                  },
                  "openai": {
                    "id": "openai",
                    "name": "OpenAI",
                    "models": {
                      "gpt-5.5": {
                        "id": "gpt-5.5",
                        "name": "GPT-5.5",
                        "tool_call": true,
                        "modalities": { "input": ["text", "image"], "output": ["text"] },
                        "limit": { "context": 1050000, "input": 922000, "output": 128000 }
                      }
                    }
                  },
                  "xai": {
                    "id": "xai",
                    "name": "xAI",
                    "models": {
                      "grok-4.7": {
                        "id": "grok-4.7",
                        "name": "Grok 4.7 catalog",
                        "tool_call": true,
                        "modalities": { "input": ["text", "image"], "output": ["text"] },
                        "limit": { "context": 500000, "input": 372000, "output": 128000 }
                      }
                    }
                  },
                  "github-copilot": {
                    "id": "github-copilot",
                    "name": "GitHub Copilot",
                    "models": {
                      "grok-4.7": {
                        "id": "grok-4.7",
                        "name": "Grok 4.7 gateway",
                        "tool_call": false,
                        "limit": { "context": 1, "output": 1 }
                      }
                    }
                  }
                }
                """),
            new ModelsDevCatalogServiceOptions());
}
