namespace CodeAlta.Agent.ModelCatalog;

/// <summary>
/// Describes a Cursor or Grok Build model from models.dev. The CLI keeps its id and its name. A limit the CLI
/// already reported is kept. What the CLI did not say — context, input and output limits, tool calling, image
/// input — is filled from the first-party catalog entry the id names.
/// </summary>
internal static class SubscriptionModelCapabilities
{
    // First-party catalogs only. A gateway that repeats the same id must not replace the model's own limits.
    private static readonly string[] Providers = ["anthropic", "openai", "xai", "google", "moonshotai"];

    private static readonly string[] Families = ["opus", "sonnet", "haiku", "fable"];

    /// <summary>
    /// Describes each model. The list is returned unchanged when <paramref name="catalog"/> is absent.
    /// </summary>
    /// <param name="models">The models the CLI listed.</param>
    /// <param name="catalog">The models.dev catalog, or null.</param>
    /// <returns>The same models, with catalog capabilities where an id is known.</returns>
    public static IReadOnlyList<AgentModelInfo> Describe(
        IReadOnlyList<AgentModelInfo> models,
        ModelsDevCatalogService? catalog)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (catalog is null || models.Count == 0)
        {
            return models;
        }

        var described = new AgentModelInfo[models.Count];
        for (var index = 0; index < models.Count; index++)
        {
            described[index] = Describe(models[index], catalog);
        }

        return described;
    }

    /// <summary>
    /// Describes one model. The instance is returned unchanged when the catalog has no entry for it.
    /// </summary>
    /// <param name="model">The model the CLI listed.</param>
    /// <param name="catalog">The models.dev catalog, or null.</param>
    /// <returns>The model, with catalog capabilities where its id is known.</returns>
    public static AgentModelInfo Describe(AgentModelInfo model, ModelsDevCatalogService? catalog)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (catalog is null)
        {
            return model;
        }

        foreach (var catalogId in CatalogIds(model.Id))
        {
            foreach (var providerId in ProviderIds(catalogId))
            {
                if (!catalog.TryGetModel(providerId, catalogId, out _))
                {
                    continue;
                }

                var described = AgentModelMetadataEnricher.EnrichModel(
                    model with { Id = catalogId },
                    catalog,
                    providerId,
                    overrides: null);
                return described with
                {
                    Id = model.Id,
                    DisplayName = model.DisplayName,
                    Description = model.Description,
                    Provider = model.Provider,
                    DefaultReasoningEffort = model.DefaultReasoningEffort,
                    SupportedReasoningEfforts = model.SupportedReasoningEfforts,
                };
            }
        }

        return model;
    }

    // Cursor prints claude-4.6-opus and claude-opus-5-thinking. models.dev lists claude-opus-4-6 and claude-opus-5.
    private static List<string> CatalogIds(string modelId)
    {
        var ids = new List<string>(4);
        Add(ids, modelId);
        var bare = StripVariant(modelId);
        Add(ids, bare);
        AddClaudeRewrites(ids, modelId);
        if (!string.Equals(bare, modelId, StringComparison.OrdinalIgnoreCase))
        {
            AddClaudeRewrites(ids, bare);
        }

        return ids;
    }

    private static void AddClaudeRewrites(List<string> ids, string modelId)
    {
        const string Prefix = "claude-";
        if (!modelId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || modelId.Length <= Prefix.Length)
        {
            return;
        }

        var tokens = modelId[Prefix.Length..].Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var familyIndex = -1;
        for (var index = 0; index < tokens.Length; index++)
        {
            if (IsFamily(tokens[index]))
            {
                familyIndex = index;
                break;
            }
        }

        if (familyIndex < 0)
        {
            return;
        }

        var version = new List<string>(tokens.Length - 1);
        for (var index = 0; index < tokens.Length; index++)
        {
            if (index != familyIndex)
            {
                version.Add(tokens[index]);
            }
        }

        if (version.Count == 0)
        {
            return;
        }

        var family = tokens[familyIndex].ToLowerInvariant();
        var joined = string.Join('-', version);
        Add(ids, $"claude-{family}-{joined}");
        Add(ids, $"claude-{family}-{joined.Replace(".", "-", StringComparison.Ordinal)}");
        if (version.TrueForAll(static token => token.Length > 0 && token.All(static character => char.IsDigit(character))))
        {
            Add(ids, $"claude-{family}-{string.Join('.', version)}");
        }
    }

    private static string StripVariant(string modelId)
    {
        var bare = modelId;
        while (true)
        {
            var stripped = StripOne(bare);
            if (stripped is null)
            {
                return bare;
            }

            bare = stripped;
        }
    }

    private static string? StripOne(string modelId)
    {
        if (modelId.EndsWith("-fast", StringComparison.OrdinalIgnoreCase) && modelId.Length > 5)
        {
            return modelId[..^5];
        }

        if (modelId.EndsWith("-thinking", StringComparison.OrdinalIgnoreCase) && modelId.Length > 9)
        {
            return modelId[..^9];
        }

        return null;
    }

    private static bool IsFamily(string token)
    {
        foreach (var family in Families)
        {
            if (token.Equals(family, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ProviderIds(string catalogId)
    {
        var preferred = Preferred(catalogId);
        if (preferred is not null)
        {
            yield return preferred;
        }

        foreach (var provider in Providers)
        {
            if (!string.Equals(provider, preferred, StringComparison.Ordinal))
            {
                yield return provider;
            }
        }
    }

    private static string? Preferred(string catalogId)
    {
        if (catalogId.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
        {
            return "anthropic";
        }

        if (catalogId.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) ||
            catalogId.StartsWith("codex", StringComparison.OrdinalIgnoreCase) ||
            catalogId.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
            catalogId.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            catalogId.StartsWith("o4", StringComparison.OrdinalIgnoreCase))
        {
            return "openai";
        }

        if (catalogId.StartsWith("grok", StringComparison.OrdinalIgnoreCase))
        {
            return "xai";
        }

        if (catalogId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return "google";
        }

        if (catalogId.StartsWith("kimi", StringComparison.OrdinalIgnoreCase))
        {
            return "moonshotai";
        }

        return null;
    }

    private static void Add(List<string> ids, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        foreach (var existing in ids)
        {
            if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        ids.Add(value);
    }
}
