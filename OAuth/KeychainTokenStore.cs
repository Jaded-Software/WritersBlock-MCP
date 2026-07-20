using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WritersBlock.Mcp.OAuth;

/// <summary>
/// macOS token store backed by the login Keychain via the <c>/usr/bin/security</c> CLI.
/// One generic-password item per authority, service name <c>writersblock-mcp:{host}</c>,
/// account <c>oauth</c>, password = the JSON-serialized <see cref="StoredTokens"/>.
/// </summary>
public sealed class KeychainTokenStore(ILogger logger) : ITokenStore
{
    private const string SecurityCli = "/usr/bin/security";
    private const string Account = "oauth";

    public string BackendDescription => "macOS Keychain (/usr/bin/security)";

    private static string ServiceName(string authorityHost) => $"writersblock-mcp:{authorityHost}";

    public StoredTokens? Load(string authorityHost)
    {
        // -w prints only the password (the JSON) to stdout; non-zero exit means "not found".
        var (exit, stdout, _) = Run(["find-generic-password", "-s", ServiceName(authorityHost), "-a", Account, "-w"]);
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
            return null;

        try
        {
            return JsonSerializer.Deserialize<StoredTokens>(stdout.Trim());
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Stored Keychain token bundle for {Host} was unreadable; ignoring.", authorityHost);
            return null;
        }
    }

    public void Save(string authorityHost, StoredTokens tokens)
    {
        var json = JsonSerializer.Serialize(tokens);
        // -U updates the item if it already exists instead of failing on a duplicate.
        var (exit, _, stderr) = Run(
        [
            "add-generic-password",
            "-U",
            "-s", ServiceName(authorityHost),
            "-a", Account,
            "-w", json,
            "-D", "WritersBlock MCP OAuth tokens",
            "-j", "WritersBlock MCP connector — access + refresh tokens"
        ]);

        if (exit != 0)
            logger.LogWarning("Failed to write tokens to Keychain for {Host} (security exit {Exit}): {Err}",
                authorityHost, exit, stderr.Trim());
    }

    public void Delete(string authorityHost)
    {
        // Ignore the exit code — "item not found" is the desired end state.
        Run(["delete-generic-password", "-s", ServiceName(authorityHost), "-a", Account]);
    }

    private (int Exit, string StdOut, string StdErr) Run(string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(SecurityCli)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in arguments)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null)
                return (-1, string.Empty, "Failed to start /usr/bin/security.");

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();
            return (proc.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Invoking /usr/bin/security failed.");
            return (-1, string.Empty, ex.Message);
        }
    }
}
