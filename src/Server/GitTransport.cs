using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

sealed class GitTransport(ForgeStore store, GitProcess git, RepositoryLocks locks)
{
    public async Task Handle(HttpContext http, string owner, string name, string endpoint)
    {
        var request = http.Request;
        var discovery = endpoint == "info/refs" && HttpMethods.IsGet(request.Method);
        var service = discovery ? request.Query["service"].ToString() : endpoint;
        if (service is not ("git-upload-pack" or "git-receive-pack") ||
            (!discovery && (!HttpMethods.IsPost(request.Method) || request.ContentType != $"application/x-{service}-request")))
        {
            http.Response.StatusCode = 404;
            return;
        }

        if (!TryAuthenticate(request))
        {
            http.Response.Headers.WWWAuthenticate = "Basic realm=\"MinGo Git\", charset=\"UTF-8\"";
            http.Response.StatusCode = 401;
            return;
        }

        var repo = owner == store.Admin && ForgeStore.ValidSlug(name) ? store.Find(name) : null;
        if (repo is null)
        {
            http.Response.StatusCode = 404;
            return;
        }
        using var lease = locks.Read(repo.Id);
        if (store.Find(repo.Id) is null)
            throw new ForgeException(404, "Repository does not exist.");

        const long requestLimit = 256L * 1024 * 1024;
        if (request.ContentLength > requestLimit)
            throw new ForgeException(413, "Git request exceeds 256 MiB.");
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
            limit.MaxRequestBodySize = requestLimit;

        var environment = new Dictionary<string, string>
        {
            ["GIT_PROJECT_ROOT"] = store.Repositories,
            ["GIT_HTTP_EXPORT_ALL"] = "1",
            ["PATH_INFO"] = $"/{repo.Id:D}.git/{endpoint}",
            ["REQUEST_METHOD"] = request.Method,
            ["QUERY_STRING"] = discovery ? $"service={service}" : "",
            ["CONTENT_TYPE"] = request.ContentType ?? "",
            ["REMOTE_USER"] = store.Admin,
            ["REMOTE_ADDR"] = http.Connection.RemoteIpAddress?.ToString() ?? ""
        };
        if (request.ContentLength is long length)
            environment["CONTENT_LENGTH"] = length.ToString(CultureInfo.InvariantCulture);
        if (request.Headers.ContentEncoding.ToString() is "gzip")
            environment["HTTP_CONTENT_ENCODING"] = "gzip";
        var protocol = request.Headers["Git-Protocol"].ToString();
        if (protocol is "version=1" or "version=2")
            environment["HTTP_GIT_PROTOCOL"] = protocol;

        await git.Execute(["http-backend"], environment, TimeSpan.FromMinutes(5), http.RequestAborted,
            async (process, token) =>
            {
                using var transfer = CancellationTokenSource.CreateLinkedTokenSource(token);
                var upload = Upload(request, process, transfer.Token);
                try
                {
                    using var output = new BufferedStream(process.StandardOutput.BaseStream, 32 * 1024);
                    var download = Download(output, http.Response, transfer.Token);
                    if (await Task.WhenAny(upload, download) == upload)
                        await upload;
                    await download;
                    await process.WaitForExitAsync(token);
                    if (process.ExitCode != 0 && http.Response.StatusCode < 400)
                        throw new ForgeException(502, "Git transport failed.");
                    return true;
                }
                finally
                {
                    await transfer.CancelAsync();
                    if (!upload.IsCompleted)
                    {
                        try { process.StandardInput.BaseStream.Close(); }
                        catch (IOException) { }
                    }
                    try { await upload; }
                    catch (OperationCanceledException) when (transfer.IsCancellationRequested) { }
                }
            });
        if (!discovery && service == "git-receive-pack")
            store.Audit("git.receive", repo.Id);
    }

    private bool TryAuthenticate(HttpRequest request)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) ||
            !header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
            header.Parameter is not { Length: > 0 and <= 512 } encoded)
            return false;
        Span<byte> decoded = stackalloc byte[384];
        if (!Convert.TryFromBase64String(encoded, decoded, out var length))
            return false;
        var credentials = Encoding.UTF8.GetString(decoded[..length]);
        var separator = credentials.IndexOf(':');
        return separator > 0 && store.CheckToken(credentials[..separator], credentials[(separator + 1)..]);
    }

    private static async Task Upload(HttpRequest request, Process process, CancellationToken token)
    {
        try
        {
            if (HttpMethods.IsPost(request.Method))
                await request.Body.CopyToAsync(process.StandardInput.BaseStream, token);
        }
        catch (IOException error) when (error is not BadHttpRequestException && !token.IsCancellationRequested) { }
        finally
        {
            process.StandardInput.Close();
        }
    }

    private static async Task Download(Stream output, HttpResponse response, CancellationToken token)
    {
        await ReadHeaders(output, response, token);
        await output.CopyToAsync(response.Body, token);
    }

    private static async Task ReadHeaders(Stream output, HttpResponse response, CancellationToken token)
    {
        var single = new byte[1];
        var line = new StringBuilder();
        for (var total = 0; total < 8192; total++)
        {
            if (await output.ReadAsync(single, token) == 0)
                break;
            if (single[0] != '\n')
            {
                line.Append((char)single[0]);
                continue;
            }
            var header = line.ToString().TrimEnd('\r');
            line.Clear();
            if (header.Length == 0)
                return;
            var separator = header.IndexOf(':');
            if (separator <= 0)
                break;
            var key = header[..separator].ToLowerInvariant();
            var value = header[(separator + 1)..].Trim();
            if (key == "status" && value.Length >= 3 && int.TryParse(value[..3], out var status) && status is >= 200 and <= 599)
                response.StatusCode = status;
            else if (key is "content-type" or "cache-control" or "expires" or "pragma")
                response.Headers[key] = value;
        }
        throw new ForgeException(502, "Invalid Git CGI response.");
    }
}
