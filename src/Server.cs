using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cocona;

var cts = new CancellationTokenSource();
var ct = cts.Token;
var pendingRequests = new ConcurrentBag<Task>();

CoconaLiteApp.Run((string? directory) =>
{
    if (!string.IsNullOrWhiteSpace(directory))
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Directory '{directory}' not found");

        Environment.CurrentDirectory = directory;
    }

    Task.Run(async () =>
    {
        var server = new TcpListener(IPAddress.Any, 4221);
        server.Start();

        while (!ct.IsCancellationRequested)
        {
            var client = await server.AcceptTcpClientAsync(ct);
            var task = HandleRequest(client, ct);
            pendingRequests.Add(task);
        }
    });
});

Console.Read();
cts.Cancel();
await Task.WhenAll(pendingRequests);
Console.WriteLine("Good bye!");
Environment.Exit(0);

async Task HandleRequest(TcpClient socket, CancellationToken cancellationToken)
{
    var notFound = "HTTP/1.1 404 Not Found\r\n\r\n"u8.ToArray();
    var ok = "HTTP/1.1 200 OK\r\n\r\n"u8.ToArray();

    try
    {
        var stream = socket.GetStream();
        var buffer = new byte[socket.Available];
        _ = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
        using var reader = new StringReader(Encoding.UTF8.GetString(buffer.ToArray()));
        var line = await reader.ReadLineAsync(cancellationToken);
        var parts = line!.Split(' ');
        var verb = parts[0];
        var url = parts[1];
        var urlFragments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Headers
        var headers = new Dictionary<string, string>();
        var sb = new StringBuilder();
        var isBody = false;

        while (await reader.ReadLineAsync(cancellationToken) is { } value)
        {
            if (string.IsNullOrWhiteSpace(value) && !isBody)
            {
                isBody = true;
            }
            
            if (!isBody)
            {
                var kv = value.Split(": ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                headers.Add(kv[0], kv[1]);
            }
            else
            {
                sb.Append(value);
            }
        }

        var response = (verb, urlFragments) switch
        {
            ("GET", []) => ok,
            ("GET", ["echo", var msg]) => Encoding.UTF8.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {msg.Length}\r\n\r\n{msg}"
            ),
            ("GET", ["user-agent"]) => Encoding.UTF8.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {headers["User-Agent"].Length}\r\n\r\n{headers["User-Agent"]}"
            ),
            ("GET", ["files", var filename]) => await GetFileAsync(filename),
            ("POST", ["files", var filename]) => await PostFileAsync(filename, sb.ToString()),
            _ => notFound
        };

        async Task<ArraySegment<byte>> PostFileAsync(string filename, string body)
        {
            var path = Path.Combine(Environment.CurrentDirectory, filename);
            await File.WriteAllTextAsync(path, body, cancellationToken);
            return Encoding.UTF8.GetBytes($"HTTP/1.1 201 Created\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        }

        async Task<ArraySegment<byte>> GetFileAsync(string filename)
        {
            var directory = Environment.CurrentDirectory;
            var path = Path.Combine(directory, filename);

            if (!File.Exists(path)) return notFound;

            var data = await File.ReadAllBytesAsync(path, cancellationToken);

            return new ArraySegment<byte>([
                ..Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {data.Length}\r\n\r\n"),
                ..data
            ]);
        }

        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(Encoding.UTF8.GetChars(response.ToArray()));
    }
    finally
    {
        socket.Dispose();
    }
}