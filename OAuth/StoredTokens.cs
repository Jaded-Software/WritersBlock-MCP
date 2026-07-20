using System.Text.Json.Serialization;

namespace WritersBlock.Mcp.OAuth;

/// <summary>
/// The token bundle persisted per-authority in the OS credential store. Includes the
/// authority and scopes alongside the tokens so a stale bundle from a different authority
/// or scope set is detected and ignored rather than silently used.
/// </summary>
public sealed class StoredTokens
{
    [JsonPropertyName("authority")]
    public string Authority { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    /// <summary>id_token, kept so `status` can surface the logged-in user without a network call.</summary>
    [JsonPropertyName("idToken")]
    public string? IdToken { get; set; }

    /// <summary>Absolute UTC expiry of the access token.</summary>
    [JsonPropertyName("accessTokenExpiration")]
    public DateTimeOffset AccessTokenExpiration { get; set; }

    /// <summary>True when the access token is within <paramref name="skew"/> of (or past) expiry.</summary>
    public bool IsExpiredOrExpiring(TimeSpan skew) =>
        DateTimeOffset.UtcNow >= AccessTokenExpiration - skew;

    public bool HasRefreshToken => !string.IsNullOrWhiteSpace(RefreshToken);
}
