using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace CodeAlta.Agent;

/// <summary>
/// Says whether the reasoning efforts a model lists are all it has. A provider sets it when it knows its list is
/// complete and sends no effort it does not list: a model that lists none then runs without one, whatever a session asked.
/// </summary>
public static class AgentReasoningEffortCapability
{
    /// <summary>The key of <see cref="AgentModelInfo.Capabilities"/> that holds <c>true</c> when the list is complete.</summary>
    public const string CompleteListKey = "reasoningEffortsComplete";

    /// <summary>
    /// The key of <see cref="AgentModelInfo.Capabilities"/> that maps an effort name to the provider id of that
    /// variant. A session that still names a variant id is that model at the mapped effort.
    /// </summary>
    public const string EffortSlugMapKey = "cursorEffortSlugs";

    /// <summary>Returns whether the provider says that the efforts <paramref name="model"/> lists are all it has.</summary>
    /// <param name="model">The model, or null when unknown.</param>
    /// <returns>True only when the provider says so; false when it does not, or for an unknown model.</returns>
    public static bool IsListComplete(AgentModelInfo? model)
        => model?.Capabilities is { } capabilities && capabilities.TryGetValue(CompleteListKey, out var value)
           && value is true or JsonElement { ValueKind: JsonValueKind.True };

    /// <summary>
    /// Finds the listed model whose effort-variant ids include <paramref name="modelId"/>.
    /// </summary>
    /// <param name="models">The models the provider lists.</param>
    /// <param name="modelId">An id that may name one model at one effort.</param>
    /// <param name="model">The listed model, when <paramref name="modelId"/> is one of its variants.</param>
    /// <param name="effort">The effort that variant is.</param>
    /// <returns>True when a listed model claims <paramref name="modelId"/> as an effort variant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="models"/> is null.</exception>
    public static bool TryMatchEffortSlug(
        IReadOnlyList<AgentModelInfo> models,
        string modelId,
        [NotNullWhen(true)] out AgentModelInfo? model,
        out AgentReasoningEffort effort)
    {
        ArgumentNullException.ThrowIfNull(models);
        foreach (var candidate in models)
        {
            if (candidate.Capabilities is { } capabilities
                && capabilities.TryGetValue(EffortSlugMapKey, out var value)
                && TryReadSlug(value, modelId, out effort))
            {
                model = candidate;
                return true;
            }
        }

        model = null;
        effort = default;
        return false;
    }

    private static bool TryReadSlug(object? value, string modelId, out AgentReasoningEffort effort)
    {
        effort = default;
        if (value is IReadOnlyDictionary<string, string> strings)
        {
            return Match(strings, modelId, out effort);
        }

        if (value is IReadOnlyDictionary<string, object?> objects)
        {
            foreach (var entry in objects)
            {
                if (entry.Value is string slug
                    && string.Equals(slug, modelId, StringComparison.Ordinal)
                    && Enum.TryParse(entry.Key, out effort))
                {
                    return true;
                }
            }

            return false;
        }

        if (value is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && string.Equals(property.Value.GetString(), modelId, StringComparison.Ordinal)
                    && Enum.TryParse(property.Name, out effort))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Match(IReadOnlyDictionary<string, string> map, string modelId, out AgentReasoningEffort effort)
    {
        foreach (var entry in map)
        {
            if (string.Equals(entry.Value, modelId, StringComparison.Ordinal) && Enum.TryParse(entry.Key, out effort))
            {
                return true;
            }
        }

        effort = default;
        return false;
    }
}
