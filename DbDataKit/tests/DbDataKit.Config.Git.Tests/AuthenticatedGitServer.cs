using System.Diagnostics;
using System.Net;
using System.Text;

namespace DbDataKit.Config.Git.Tests;

/// <summary>
/// A stand-in git server that requires a login (plan 13, W12): the real <c>git http-backend</c> behind a small HTTP listener that answers 401 until the request carries basic authentication with the right user and token. A bare repository over
/// <c>file://</c> cannot test authentication, so this speaks the smart HTTP protocol over a socket. Bare repositories live in <see cref="Root"/>; the address of <c>name.git</c> is <c>BaseAddress + "name.git"</c>. Every request is
/// recorded (<see cref="Requests"/>) with whether it was authorised, so a test can tell "refused without it" from "never reached".
/// </summary>
public sealed class AuthenticatedGitServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly string _expected;

    public string Root { get; } = Directory.CreateTempSubdirectory("dbdata-git-http-").FullName;
    public string User { get; }
    public string Token { get; }
    public string BaseAddress { get; }
    public List<(string Method, string Path, bool Authorised)> Requests { get; } = [];

    public AuthenticatedGitServer(string user, string token)
    {
        (User, Token) = (user, token);
        _expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
        (_listener, BaseAddress) = StartListener();
        _loop = Task.Run(Serve);
    }

    /// <summary>Makes an empty bare repository <c>name.git</c> that accepts pushes from an authenticated user.</summary>
    public string CreateBare(string name)
    {
        var path = Path.Combine(Root, name + ".git");
        LibGit2Sharp.Repository.Init(path, isBare: true);
        return BaseAddress + name + ".git";
    }

    public string PathOf(string name) => Path.Combine(Root, name + ".git");

    /// <summary>
    /// An <see cref="HttpListener"/> needs its address before it starts, so a port picked first can be taken by another process before the listener binds it ("address already in use");
    /// the whole start is tried again on a new port, up to five times.
    /// </summary>
    private static (HttpListener Listener, string BaseAddress) StartListener()
    {
        for (var attempt = 1; ; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var address = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(address);
            try
            {
                listener.Start();
                return (listener, address);
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }
    }

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var authorised = request.Headers["Authorization"] == _expected;
            lock (Requests) Requests.Add((request.HttpMethod, request.Url!.AbsolutePath, authorised));
            if (!authorised)
            {
                context.Response.StatusCode = 401;
                context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"git\"");
                context.Response.Close();
                return;
            }

            using var body = new MemoryStream();
            await request.InputStream.CopyToAsync(body);
            var start = new ProcessStartInfo("git", "http-backend") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.Environment["GIT_PROJECT_ROOT"] = Root;
            start.Environment["GIT_HTTP_EXPORT_ALL"] = "1";
            start.Environment["REQUEST_METHOD"] = request.HttpMethod;
            start.Environment["PATH_INFO"] = Uri.UnescapeDataString(request.Url!.AbsolutePath);
            start.Environment["QUERY_STRING"] = request.Url.Query.TrimStart('?');
            start.Environment["CONTENT_TYPE"] = request.ContentType ?? "";
            start.Environment["CONTENT_LENGTH"] = body.Length.ToString();
            start.Environment["REMOTE_USER"] = User;
            start.Environment["REMOTE_ADDR"] = "127.0.0.1";
            if (request.Headers["Content-Encoding"] is { } encoding) start.Environment["HTTP_CONTENT_ENCODING"] = encoding;
            if (request.Headers["Git-Protocol"] is { } protocol) start.Environment["GIT_PROTOCOL"] = protocol;
            using var process = Process.Start(start)!;
            _ = process.StandardError.ReadToEndAsync();
            await process.StandardInput.BaseStream.WriteAsync(body.ToArray());
            process.StandardInput.Close();
            using var output = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(output);
            await process.WaitForExitAsync();

            // a CGI answer: headers, a blank line, the body
            var bytes = output.ToArray();
            var split = IndexOf(bytes, "\r\n\r\n"u8) is var crlf and >= 0 ? (crlf, 4) : (IndexOf(bytes, "\n\n"u8), 2);
            var head = Encoding.ASCII.GetString(bytes, 0, split.Item1 < 0 ? 0 : split.Item1);
            foreach (var line in head.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var (name, value) = (line[..colon].Trim(), line[(colon + 1)..].Trim());
                if (name.Equals("Status", StringComparison.OrdinalIgnoreCase)) context.Response.StatusCode = int.Parse(value.Split(' ')[0]);
                else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) context.Response.ContentType = value;
                else context.Response.AddHeader(name, value);
            }
            var offset = split.Item1 < 0 ? 0 : split.Item1 + split.Item2;
            context.Response.ContentLength64 = bytes.Length - offset;
            await context.Response.OutputStream.WriteAsync(bytes.AsMemory(offset));
            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            try { context.Response.Abort(); } catch (ObjectDisposedException) { }
        }
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
