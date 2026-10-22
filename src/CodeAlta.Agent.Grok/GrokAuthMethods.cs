using System.Text.Json;

namespace CodeAlta.Agent.Grok;

/// <summary>
/// Chooses the ACP <c>authenticate</c> method from the <c>initialize</c> result of <c>grok agent stdio</c>.
/// </summary>
internal static class GrokAuthMethods
{
    /// <summary>The method id of a <c>grok login</c> already stored by the CLI.</summary>
    internal const string CachedToken = "cached_token";

    /// <summary>The method id used when <c>XAI_API_KEY</c> is set.</summary>
    internal const string ApiKey = "xai.api_key";

    /// <summary>
    /// Picks <see cref="ApiKey"/> when that method is offered and <paramref name="hasApiKey"/> is true, otherwise
    /// <see cref="CachedToken"/> when the CLI offers it.
    /// </summary>
    /// <param name="initializeResponse">The JSON-RPC response of <c>initialize</c>.</param>
    /// <param name="hasApiKey"><see langword="true"/> when <c>XAI_API_KEY</c> is set for this process.</param>
    /// <returns>The method id, or null when the CLI offers neither.</returns>
    internal static string? Select(JsonElement initializeResponse, bool hasApiKey)
    {
        var result = initializeResponse.ValueKind == JsonValueKind.Object && initializeResponse.TryGetProperty("result", out var nested)
            ? nested
            : initializeResponse;
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("authMethods", out var methods) || methods.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var offered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in methods.EnumerateArray())
        {
            var id = method.ValueKind == JsonValueKind.String
                ? method.GetString()
                : method.ValueKind == JsonValueKind.Object && method.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.String
                    ? idProperty.GetString()
                    : null;
            if (!string.IsNullOrWhiteSpace(id))
            {
                offered.Add(id);
            }
        }

        if (hasApiKey && offered.Contains(ApiKey))
        {
            return ApiKey;
        }

        if (offered.Contains(CachedToken))
        {
            return CachedToken;
        }

        return offered.Contains(ApiKey) ? ApiKey : null;
    }

    /// <summary>Reports whether this process has <c>XAI_API_KEY</c>.</summary>
    /// <returns><see langword="true"/> when the variable is set to a non-blank value.</returns>
    internal static bool HasApiKey()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY"));
}
