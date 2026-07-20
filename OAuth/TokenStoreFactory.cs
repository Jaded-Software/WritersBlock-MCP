using Microsoft.Extensions.Logging;

namespace WritersBlock.Mcp.OAuth;

/// <summary>
/// Selects the OS-appropriate <see cref="ITokenStore"/>: Keychain on macOS, DPAPI on Windows,
/// permission-restricted plain file elsewhere.
/// </summary>
public static class TokenStoreFactory
{
    public static ITokenStore Create(ILogger logger)
    {
        if (OperatingSystem.IsMacOS())
            return new KeychainTokenStore(logger);

        if (OperatingSystem.IsWindows())
            return new DpapiTokenStore(logger);

        return new PlainFileTokenStore(logger);
    }
}
