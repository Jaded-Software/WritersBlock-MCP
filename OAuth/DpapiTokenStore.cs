using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WritersBlock.Mcp.OAuth;

/// <summary>
/// Windows token store: the JSON bundle is encrypted with DPAPI (CurrentUser scope) and written
/// to a per-authority file under LocalApplicationData. Only the logged-in Windows user can decrypt.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStore(ILogger logger) : ITokenStore
{
    // Bound to the current user; ties decryption to this Windows account.
    private static readonly byte[] Entropy = "writersblock-mcp/v1"u8.ToArray();

    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WritersBlock.Mcp", "tokens");

    public string BackendDescription => "Windows DPAPI (CurrentUser)";

    private string FilePath(string authorityHost) =>
        Path.Combine(_dir, $"{SanitizeHost(authorityHost)}.dpapi");

    public StoredTokens? Load(string authorityHost)
    {
        var path = FilePath(authorityHost);
        try
        {
            if (!File.Exists(path)) return null;
            var protectedBytes = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredTokens>(Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stored DPAPI token bundle for {Host} was unreadable; ignoring.", authorityHost);
            return null;
        }
    }

    public void Save(string authorityHost, StoredTokens tokens)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var plain = JsonSerializer.SerializeToUtf8Bytes(tokens);
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath(authorityHost), protectedBytes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write DPAPI-protected tokens for {Host}.", authorityHost);
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
            logger.LogWarning(ex, "Failed to delete DPAPI token file for {Host}.", authorityHost);
        }
    }

    private static string SanitizeHost(string host)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(host.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
