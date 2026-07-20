using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Duende.IdentityModel.OidcClient.Browser;
using Microsoft.Extensions.Logging;

namespace WritersBlock.Mcp.OAuth;

/// <summary>
/// RFC 8252 native-app browser: opens the authorize URL in the system browser, listens on a
/// pre-bound loopback <see cref="HttpListener"/> for the OAuth callback, and hands the callback
/// query string back to OidcClient for the code+PKCE exchange. The listener is bound to a fixed
/// contract port before OidcClient is constructed (the redirect_uri, and therefore the port, is
/// baked into the authorize URL), so this type owns and disposes the listener.
/// </summary>
public sealed class LoopbackBrowser(HttpListener listener, string redirectUri, ILogger logger)
    : IBrowser, IDisposable
{
    private const string CompletePageHtml =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>WritersBlock MCP</title>" +
        "<style>body{font-family:-apple-system,Segoe UI,Roboto,sans-serif;background:#f5f5f7;" +
        "color:#1d1d1f;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}" +
        ".card{background:#fff;padding:2.5rem 3rem;border-radius:14px;box-shadow:0 6px 24px rgba(0,0,0,.08);" +
        "text-align:center;max-width:26rem}h1{font-size:1.25rem;margin:0 0 .5rem}" +
        "p{margin:0;color:#555;line-height:1.5}</style></head><body><div class=\"card\">" +
        "<h1>Login complete</h1><p>You can close this tab and return to your MCP client.</p>" +
        "</div></body></html>";

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken)
    {
        try
        {
            OpenSystemBrowser(options.StartUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to launch the system browser. Open this URL manually to sign in:\n{Url}", options.StartUrl);
            // Still wait for the callback — the user can paste the URL into a browser themselves.
        }

        try
        {
            var context = await GetContextAsync(listener, cancellationToken);
            var request = context.Request;

            // Everything after '?' is what OidcClient needs to validate state + exchange the code.
            var responseQuery = request.Url?.Query ?? string.Empty;

            await WriteCompletePageAsync(context.Response, cancellationToken);

            if (string.IsNullOrEmpty(responseQuery))
            {
                logger.LogError("The loopback callback carried no query string.");
                return new BrowserResult { ResultType = BrowserResultType.UnknownError };
            }

            return new BrowserResult
            {
                ResultType = BrowserResultType.Success,
                Response = redirectUri + responseQuery
            };
        }
        catch (OperationCanceledException)
        {
            return new BrowserResult { ResultType = BrowserResultType.Timeout };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loopback callback listener failed.");
            return new BrowserResult { ResultType = BrowserResultType.UnknownError };
        }
    }

    /// <summary>
    /// <see cref="HttpListener.GetContextAsync"/> ignores cancellation; bridge it by aborting
    /// the listener when the token trips (login timeout) so the await unblocks.
    /// </summary>
    private static async Task<HttpListenerContext> GetContextAsync(HttpListener listener, CancellationToken ct)
    {
        var contextTask = listener.GetContextAsync();
        await using (ct.Register(() =>
        {
            try { listener.Abort(); } catch { /* listener already gone */ }
        }))
        {
            try
            {
                return await contextTask;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
        }
    }

    private static async Task WriteCompletePageAsync(HttpListenerResponse response, CancellationToken ct)
    {
        try
        {
            var buffer = Encoding.UTF8.GetBytes(CompletePageHtml);
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, ct);
            response.OutputStream.Close();
        }
        catch
        {
            // Best effort — the tokens are already captured from the query string.
        }
    }

    private void OpenSystemBrowser(string url)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // UseShellExecute lets the OS resolve the default browser without a shell string.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else
        {
            Process.Start("xdg-open", url);
        }

        logger.LogInformation("Opened the system browser for sign-in. Complete the login in the browser window.");
    }

    public void Dispose()
    {
        try
        {
            if (listener.IsListening)
                listener.Stop();
            listener.Close();
        }
        catch
        {
            // ignore
        }
    }
}
