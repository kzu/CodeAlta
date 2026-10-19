using System.Globalization;
using System.Text;

namespace CodeAlta.Agent.Cursor;

/// <summary>
/// Collapses the Cursor CLI's one-id-per-effort inventory into one model with a reasoning-effort list, and turns a
/// chosen effort back into the id <c>--model</c> accepts.
/// </summary>
internal static class CursorModelEfforts
{
    /// <summary>The capability that maps an effort name to the CLI id of that variant.</summary>
    internal const string SlugMapKey = AgentReasoningEffortCapability.EffortSlugMapKey;

    private const string FastSuffix = "-fast";
    private const string ThinkingSuffix = "-thinking";

    // Longer suffixes first: <c>-xhigh</c> and <c>-extra-high</c> also end in <c>-high</c>.
    private static readonly (string Suffix, AgentReasoningEffort Effort)[] EffortSuffixes =
    [
        ("extra-high", AgentReasoningEffort.XHigh),
        ("xhigh", AgentReasoningEffort.XHigh),
        ("minimal", AgentReasoningEffort.Minimal),
        ("medium", AgentReasoningEffort.Medium),
        ("none", AgentReasoningEffort.None),
        ("high", AgentReasoningEffort.High),
        ("max", AgentReasoningEffort.Max),
        ("low", AgentReasoningEffort.Low),
    ];

    private static readonly AgentReasoningEffort[] Canonical =
    [
        AgentReasoningEffort.None,
        AgentReasoningEffort.Minimal,
        AgentReasoningEffort.Low,
        AgentReasoningEffort.Medium,
        AgentReasoningEffort.High,
        AgentReasoningEffort.XHigh,
        AgentReasoningEffort.Max,
    ];

    /// <summary>
    /// Groups rows that differ only by a reasoning-effort suffix. Fast and thinking stay on the model id.
    /// </summary>
    /// <param name="listed">The models in the order the CLI printed them.</param>
    /// <returns>One model per family, in the order each family first appeared.</returns>
    internal static IReadOnlyList<AgentModelInfo> Group(IReadOnlyList<AgentModelInfo> listed)
    {
        ArgumentNullException.ThrowIfNull(listed);
        if (listed.Count == 0)
        {
            return [];
        }

        var parsed = new List<Variant>(listed.Count);
        foreach (var model in listed)
        {
            parsed.Add(Parse(model.Id, model.DisplayName ?? model.Id));
        }

        ReleaseBases(parsed);
        var groups = new List<List<Variant>>();
        var indexByKey = new Dictionary<GroupKey, int>();
        foreach (var variant in parsed)
        {
            var key = new GroupKey(variant.Base, variant.Fast, variant.Thinking);
            if (!indexByKey.TryGetValue(key, out var index))
            {
                index = groups.Count;
                indexByKey[key] = index;
                groups.Add([]);
            }

            groups[index].Add(variant);
        }

        foreach (var group in groups)
        {
            AssignBareMedium(group);
        }

        var models = new List<AgentModelInfo>(groups.Count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            // A variant with no effort of its own stays listed on its own id. Grouping must not hide it.
            if (group.Exists(static variant => variant.Effort is null) || !KeepEfforts(group, groups))
            {
                foreach (var variant in group)
                {
                    Add(models, used, Ungrouped(variant));
                }

                continue;
            }

            var published = Publish(group);
            if (used.Add(published.Id))
            {
                models.Add(published);
                continue;
            }

            foreach (var variant in group)
            {
                Add(models, used, Ungrouped(variant));
            }
        }

        return models;
    }

    /// <summary>
    /// The id <c>--model</c> receives. A grouped model contributes the variant of <paramref name="effort"/>, or its
    /// default variant when the request names none. An id that is already a variant is kept when no effort is asked.
    /// </summary>
    /// <param name="modelId">The model the session selected.</param>
    /// <param name="effort">The reasoning effort the session selected.</param>
    /// <param name="model">The catalog model, when the session resolved one.</param>
    /// <returns>The CLI id, or empty when <paramref name="modelId"/> is empty.</returns>
    internal static string Select(string? modelId, AgentReasoningEffort? effort, AgentModelInfo? model)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return "";
        }

        var id = modelId.Trim();
        if (!Applies(model, id, out var map))
        {
            return id;
        }

        if (effort is { } asked && map.TryGetValue(asked.ToString(), out var slug))
        {
            return slug;
        }

        if (map.Values.Contains(id, StringComparer.Ordinal))
        {
            return id;
        }

        if (model!.DefaultReasoningEffort is { } fallback && map.TryGetValue(fallback.ToString(), out var fallbackSlug))
        {
            return fallbackSlug;
        }

        return id;
    }

    private static void Add(List<AgentModelInfo> models, HashSet<string> used, AgentModelInfo model)
    {
        if (used.Add(model.Id))
        {
            models.Add(model);
        }
    }

    private static bool Applies(AgentModelInfo? model, string id, out Dictionary<string, string> map)
    {
        map = [];
        if (model?.Capabilities is not { } capabilities || !capabilities.TryGetValue(SlugMapKey, out var value))
        {
            return false;
        }

        map = ReadMap(value);
        if (map.Count == 0)
        {
            return false;
        }

        return string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase)
            || map.Values.Contains(id, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ReadMap(object? value)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (value is IReadOnlyDictionary<string, string> strings)
        {
            foreach (var entry in strings)
            {
                map[entry.Key] = entry.Value;
            }
        }
        else if (value is IReadOnlyDictionary<string, object?> objects)
        {
            foreach (var entry in objects)
            {
                if (entry.Value is string slug)
                {
                    map[entry.Key] = slug;
                }
            }
        }

        return map;
    }

    // An id that is itself the base of another effort variant keeps its suffix: gpt-5.1-codex-max is a model, and
    // gpt-5.1-codex-max-low is that model at low effort.
    private static void ReleaseBases(List<Variant> variants)
    {
        var bases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            if (variant.Effort is not null)
            {
                bases.Add(variant.Base);
            }
        }

        foreach (var variant in variants)
        {
            if (variant.Effort is null || !bases.Contains(variant.Core))
            {
                continue;
            }

            variant.Base = variant.Core;
            variant.Effort = null;
            variant.Thinking = variant.Core.EndsWith(ThinkingSuffix, StringComparison.Ordinal)
                ? ThinkingPlace.Before
                : ThinkingPlace.None;
        }
    }

    // A bare id beside low and high (gpt-5.3-codex, gpt-5.1) is the medium variant the CLI leaves unsuffixed.
    private static void AssignBareMedium(List<Variant> group)
    {
        var bare = group.FindIndex(static variant => variant.Effort is null);
        if (bare < 0 || group.Exists(static variant => variant.Effort == AgentReasoningEffort.Medium))
        {
            return;
        }

        var hasBelow = group.Exists(static variant => variant.Effort is AgentReasoningEffort.None or AgentReasoningEffort.Minimal or AgentReasoningEffort.Low);
        var hasAbove = group.Exists(static variant => variant.Effort is AgentReasoningEffort.High or AgentReasoningEffort.XHigh or AgentReasoningEffort.Max);
        if (hasBelow && hasAbove)
        {
            group[bare].Effort = AgentReasoningEffort.Medium;
        }
    }

    private static bool KeepEfforts(List<Variant> group, List<List<Variant>> groups)
    {
        var efforts = group.Where(static variant => variant.Effort is not null).Select(static variant => variant.Effort!.Value).Distinct().ToArray();
        if (efforts.Length == 0)
        {
            return false;
        }

        if (efforts.Length >= 2)
        {
            return true;
        }

        var sample = group[0];
        if (groups.Exists(other => other != group && other[0].Base == sample.Base))
        {
            return true;
        }

        var effort = efforts[0];
        return group.Exists(variant => variant.Effort == effort && ContainsPhrase(variant.Name, Label(effort)));
    }

    private static AgentModelInfo Publish(List<Variant> group)
    {
        var efforts = Canonical.Where(effort => group.Exists(variant => variant.Effort == effort)).ToArray();
        var fallback = ChooseDefault(group, efforts);
        var source = group.Find(variant => variant.Effort == fallback) ?? group[0];
        var name = DisplayName(source, group[0]);
        var id = ModelId(group[0]);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var effort in efforts)
        {
            var variant = group.Find(candidate => candidate.Effort == effort);
            if (variant is not null)
            {
                map[effort.ToString()] = variant.Id;
            }
        }

        return new AgentModelInfo(
            id,
            name,
            Provider: CursorModelProviderRuntime.ProviderType,
            DefaultReasoningEffort: fallback,
            SupportedReasoningEfforts: efforts,
            Capabilities: Capabilities(map));
    }

    private static AgentModelInfo Ungrouped(Variant variant)
    {
        return new AgentModelInfo(
            variant.Id,
            variant.Name,
            Provider: CursorModelProviderRuntime.ProviderType,
            Capabilities: Capabilities(null));
    }

    private static Dictionary<string, object?> Capabilities(Dictionary<string, string>? slugs)
    {
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [AgentReasoningEffortCapability.CompleteListKey] = true,
        };
        if (slugs is { Count: > 0 })
        {
            capabilities[SlugMapKey] = slugs;
            capabilities["supportsReasoning"] = true;
        }

        return capabilities;
    }

    private static AgentReasoningEffort ChooseDefault(List<Variant> group, AgentReasoningEffort[] efforts)
    {
        var omitted = efforts.Where(effort => group.Exists(variant =>
            variant.Effort == effort && !ContainsPhrase(variant.Name, Label(effort)))).ToArray();
        return Prefer(omitted.Length > 0 ? omitted : efforts);
    }

    private static AgentReasoningEffort Prefer(IReadOnlyList<AgentReasoningEffort> efforts)
    {
        if (efforts.Contains(AgentReasoningEffort.High))
        {
            return AgentReasoningEffort.High;
        }

        if (efforts.Contains(AgentReasoningEffort.Medium))
        {
            return AgentReasoningEffort.Medium;
        }

        for (var index = Canonical.Length - 1; index >= 0; index--)
        {
            if (efforts.Contains(Canonical[index]))
            {
                return Canonical[index];
            }
        }

        return efforts[0];
    }

    private static string DisplayName(Variant source, Variant shape)
    {
        var name = source.Effort is { } effort ? WithoutPhrase(source.Name, Label(effort)) : source.Name;
        if (shape.Thinking != ThinkingPlace.None && !ContainsPhrase(name, "Thinking"))
        {
            name = name + " Thinking";
        }

        if (shape.Fast && !ContainsPhrase(name, "Fast"))
        {
            name = name + " Fast";
        }

        return name;
    }

    private static string ModelId(Variant shape)
    {
        var id = shape.Base;
        if (shape.Thinking != ThinkingPlace.None)
        {
            id += ThinkingSuffix;
        }

        if (shape.Fast)
        {
            id += FastSuffix;
        }

        return id;
    }

    private static Variant Parse(string id, string name)
    {
        var cleaned = Normalize(name);
        var fast = id.EndsWith(FastSuffix, StringComparison.Ordinal) && id.Length > FastSuffix.Length;
        var core = fast ? id[..^FastSuffix.Length] : id;
        if (TryPeel(core, out var effort, out var rest) && rest.Length > 0)
        {
            if (rest.EndsWith(ThinkingSuffix, StringComparison.Ordinal) && rest.Length > ThinkingSuffix.Length)
            {
                return new Variant(id, core, cleaned, rest[..^ThinkingSuffix.Length], fast, ThinkingPlace.Before, effort);
            }

            return new Variant(id, core, cleaned, rest, fast, ThinkingPlace.None, effort);
        }

        if (core.EndsWith(ThinkingSuffix, StringComparison.Ordinal) && core.Length > ThinkingSuffix.Length)
        {
            var inner = core[..^ThinkingSuffix.Length];
            if (inner.Length > 0 && TryPeel(inner, out effort, out rest) && rest.Length > 0)
            {
                return new Variant(id, core, cleaned, rest, fast, ThinkingPlace.After, effort);
            }

            if (inner.Length > 0)
            {
                return new Variant(id, core, cleaned, inner, fast, ThinkingPlace.Before, null);
            }
        }

        return new Variant(id, core, cleaned, core, fast, ThinkingPlace.None, null);
    }

    private static bool TryPeel(string id, out AgentReasoningEffort effort, out string rest)
    {
        foreach (var (suffix, known) in EffortSuffixes)
        {
            var token = "-" + suffix;
            if (id.Length > token.Length && id.EndsWith(token, StringComparison.Ordinal))
            {
                effort = known;
                rest = id[..^token.Length];
                return true;
            }
        }

        effort = default;
        rest = id;
        return false;
    }

    private static string Label(AgentReasoningEffort effort)
        => effort switch
        {
            AgentReasoningEffort.None => "None",
            AgentReasoningEffort.Minimal => "Minimal",
            AgentReasoningEffort.Low => "Low",
            AgentReasoningEffort.Medium => "Medium",
            AgentReasoningEffort.High => "High",
            AgentReasoningEffort.XHigh => "Extra High",
            AgentReasoningEffort.Max => "Max",
            _ => effort.ToString(),
        };

    private static string WithoutPhrase(string name, string phrase)
    {
        var index = 0;
        while ((index = IndexOfPhrase(name, phrase, index)) >= 0)
        {
            name = string.Concat(name.AsSpan(0, index), name.AsSpan(index + phrase.Length));
            index = Math.Max(0, index);
        }

        return Normalize(name);
    }

    private static bool ContainsPhrase(string name, string phrase)
        => IndexOfPhrase(name, phrase, 0) >= 0;

    private static int IndexOfPhrase(string name, string phrase, int start)
    {
        while ((start = name.IndexOf(phrase, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = start == 0 || !char.IsLetterOrDigit(name[start - 1]);
            var after = start + phrase.Length >= name.Length || !char.IsLetterOrDigit(name[start + phrase.Length]);
            if (before && after)
            {
                return start;
            }

            start += phrase.Length;
        }

        return -1;
    }

    private static string Normalize(string name)
    {
        var builder = new StringBuilder(name.Length);
        var pendingSpace = false;
        foreach (var character in name)
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        var text = builder.ToString().Trim();
        foreach (var suffix in new[] { " (current, default)", " (default, current)", " (current)", " (default)" })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return text[..^suffix.Length].TrimEnd();
            }
        }

        return text;
    }

    private enum ThinkingPlace
    {
        None,
        Before,
        After,
    }

    private readonly record struct GroupKey(string Base, bool Fast, ThinkingPlace Thinking);

    private sealed class Variant(
        string id,
        string core,
        string name,
        string @base,
        bool fast,
        ThinkingPlace thinking,
        AgentReasoningEffort? effort)
    {
        public string Id { get; } = id;

        public string Core { get; } = core;

        public string Name { get; } = name;

        public string Base { get; set; } = @base;

        public bool Fast { get; } = fast;

        public ThinkingPlace Thinking { get; set; } = thinking;

        public AgentReasoningEffort? Effort { get; set; } = effort;
    }
}
