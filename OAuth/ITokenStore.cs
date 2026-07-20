namespace WritersBlock.Mcp.OAuth;

/// <summary>
/// Per-authority persistence for the OAuth token bundle. Implementations back onto the
/// OS-native secret store where possible (macOS Keychain, Windows DPAPI) and fall back to a
/// permission-restricted plain file elsewhere. All operations are best-effort and must never
/// throw for a missing entry (<see cref="Load"/> returns null; <see cref="Delete"/> is a no-op).
/// </summary>
public interface ITokenStore
{
    /// <summary>Human-readable name of the backing store, for the <c>status</c> verb and warnings.</summary>
    string BackendDescription { get; }

    /// <summary>Reads the stored bundle for the given authority host, or null if none/unreadable.</summary>
    StoredTokens? Load(string authorityHost);

    /// <summary>Persists (replacing) the bundle for the given authority host.</summary>
    void Save(string authorityHost, StoredTokens tokens);

    /// <summary>Removes any stored bundle for the given authority host. No-op if absent.</summary>
    void Delete(string authorityHost);
}
