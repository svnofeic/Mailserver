using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mailserver.Core.Security.Acme;

/// <summary>Pending http-01 answers (token → key authorization), shared with the web interface if it listens on port 80.</summary>
public sealed class AcmeChallengeStore
{
    private readonly ConcurrentDictionary<string, string> _answers = new(StringComparer.Ordinal);

    public void Add(string token, string keyAuthorization) => _answers[token] = keyAuthorization;

    public void Remove(string token) => _answers.TryRemove(token, out _);

    public string? Find(string token) => _answers.GetValueOrDefault(token);

    public const string PathPrefix = "/.well-known/acme-challenge/";
}

/// <summary>
/// A tiny HTTP server that only answers /.well-known/acme-challenge/… and runs only while a certificate is being issued,
/// so port 80 is not occupied otherwise.
/// </summary>
public sealed class AcmeHttpChallengeServer : IAsyncDisposable
{
    private readonly List<TcpListener> _listeners = [];
    private readonly AcmeChallengeStore _store;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];

    private AcmeHttpChallengeServer(AcmeChallengeStore store) => _store = store;

    public static AcmeHttpChallengeServer Start(AcmeChallengeStore store, int port)
    {
        var server = new AcmeHttpChallengeServer(store);
        try
        {
            server.Listen(IPAddress.Any, port);
        }
        catch (SocketException ex)
        {
            throw new AcmeException($"Port {port} lässt sich nicht öffnen ({ex.SocketErrorCode}). Läuft dort ein anderer Webserver " +
                                    "(z. B. IIS)? Dann einen Challenge-Ordner angeben oder den anderen Webserver kurz anhalten.");
        }

        try
        {
            server.Listen(IPAddress.IPv6Any, port);
        }
        catch (SocketException)
        {
            // No IPv6 on this machine; IPv4 is enough.
        }

        return server;
    }

    private void Listen(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            listener.Server.DualMode = false;
        }

        listener.Start();
        _listeners.Add(listener);
        _loops.Add(AcceptLoopAsync(listener));
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var requestLine = await reader.ReadLineAsync(timeout.Token) ?? "";
                // Headers are not needed; read them so the client is not reset before it sent everything.
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token)))
                {
                }

                var parts = requestLine.Split(' ');
                var path = parts.Length >= 2 ? parts[1] : "";
                var answer = parts[0] is "GET" or "HEAD" && path.StartsWith(AcmeChallengeStore.PathPrefix, StringComparison.Ordinal)
                    ? _store.Find(path[AcmeChallengeStore.PathPrefix.Length..])
                    : null;
                var body = Encoding.ASCII.GetBytes(answer ?? "Not found");
                var head = $"HTTP/1.1 {(answer is null ? "404 Not Found" : "200 OK")}\r\nContent-Type: text/plain\r\n" +
                           $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token);
                if (parts[0] != "HEAD")
                {
                    await stream.WriteAsync(body, timeout.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        await Task.WhenAll(_loops);
        _stop.Dispose();
    }
}

/// <summary>Writes challenge files into the web root of another web server (e.g. IIS) that answers on port 80.</summary>
public sealed class AcmeChallengeDirectory(string webRoot)
{
    // IIS serves files without extension only with a MIME mapping.
    private const string WebConfig = """
        <?xml version="1.0" encoding="UTF-8"?>
        <configuration>
          <system.webServer>
            <staticContent>
              <clear />
              <mimeMap fileExtension="." mimeType="text/plain" />
            </staticContent>
            <handlers>
              <clear />
              <add name="StaticFile" path="*" verb="*" modules="StaticFileModule" resourceType="Either" requireAccess="Read" />
            </handlers>
          </system.webServer>
        </configuration>
        """;

    private string Folder => Path.Combine(webRoot, ".well-known", "acme-challenge");

    public void Write(string token, string keyAuthorization)
    {
        Directory.CreateDirectory(Folder);
        var config = Path.Combine(Folder, "web.config");
        if (!File.Exists(config))
        {
            File.WriteAllText(config, WebConfig);
        }

        File.WriteAllText(Path.Combine(Folder, token), keyAuthorization, new UTF8Encoding(false));
    }

    public void Delete(string token)
    {
        try
        {
            File.Delete(Path.Combine(Folder, token));
        }
        catch (IOException)
        {
        }
    }
}
