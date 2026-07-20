using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WritersBlock.Mcp.OAuth;

/// <summary>
/// Fallback token store for OSes without a supported native secret store (e.g. Linux).
/// Writes the JSON bundle to a per-authority file under LocalApplicationData with owner-only
/// (0600) permissions and warns on stderr that tokens are NOT OS-encrypted at rest.
/// </summary>
public sealed class PlainFileTokenStore : ITokenStore
{
    private readonly ILogger _logger;
    private readonly string _dir;
    private bool _warned;

    public PlainFileTokenStore(ILogger logger)
    {
        _logger = logger;
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WritersBlock.Mcp", "tokens");
    }

    public string BackendDescription => $"plain file (0600) at {_dir} — NOT OS-encrypted";

    private string FilePath(string authorityHost) =>
        Path.Combine(_dir, $"{SanitizeHost(authorityHost)}.json");

    public StoredTokens? Load(string authorityHost)
    {
        var path = FilePath(authorityHost);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<StoredTokens>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stored token file for {Host} was unreadable; ignoring.", authorityHost);
            return null;
        }
    }

    public void Save(string authorityHost, StoredTokens tokens)
    {
        WarnOnce();
        try
        {
            Directory.CreateDirectory(_dir);
            var path = FilePath(authorityHost);
            File.WriteAllText(path, JsonSerializer.Serialize(tokens));
            TryRestrictPermissions(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write token file for {Host}.", authorityHost);
        }
    }

    public void Delete(string authorityHost)
    {
        try
        {
            var path = FilePath(authorityHost);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete token file for {Host}.", authorityHost);
        }
    }

    private void WarnOnce()
    {
        if (_warned) return;
        _warned = true;
        _logger.LogWarning(
            "No OS-native secret store on this platform. OAuth tokens are stored in a plain, owner-only (0600) file " +
            "under {Dir}. Treat this machine's user account as the trust boundary.", _dir);
    }

    private void TryRestrictPermissions(string path)
    {
        // Unix file modes are meaningless (and unsupported) on Windows; this store is only
        // selected on non-Windows platforms, but guard anyway to keep the call site clean.
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            // 0600 — owner read/write only.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set 0600 permissions on {Path}.", path);
        }
    }

    private static string SanitizeHost(string host)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(host.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
