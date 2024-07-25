using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
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
            //var client = await server.AcceptTcpClientAsync(ct);
            var socket = await server.AcceptSocketAsync(); // wait for client
            var task = HandleRequest(socket, ct);
            pendingRequests.Add(task);
        }
    });
});

Console.Read();
cts.Cancel();
await Task.WhenAll(pendingRequests);
Console.WriteLine("Good bye!");
Environment.Exit(0);

async Task HandleRequest(Socket socket, CancellationToken cancellationToken)
{
    var newLine = "\r\n"u8.ToArray();
    var notFound = "HTTP/1.1 404 Not Found\r\n\r\n"u8.ToArray();
    var ok = "HTTP/1.1 200 OK\r\n"u8.ToArray();
    var contentEncodingGzip = "Content-Encoding: gzip"u8.ToArray();
    const string acceptEncoding = "Accept-Encoding";
    const int bufferSize = 1024;
    var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

    try
    {
        await socket.ReceiveAsync(buffer, cancellationToken);
        using var reader = new StringReader(Encoding.UTF8.GetString(buffer));
        var line = await reader.ReadLineAsync(cancellationToken);

        // Headers
        var headers = new Dictionary<string, string>();
        var parts = line!.Split(' ');
        var verb = parts[0];
        var url = parts[1];
        var urlFragments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
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
                sb.Append(value.Replace("\0", string.Empty));
            }
        }

        var response = (verb, urlFragments) switch
        {
            ("GET", []) => await OkAsync(),
            ("GET", ["echo", var msg]) => EchoAsync(msg),
            ("GET", ["user-agent"]) => Encoding.UTF8.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {headers["User-Agent"].Length}\r\n\r\n{headers["User-Agent"]}"),
            ("GET", ["files", var filename]) => await GetFileAsync(filename),
            ("POST", ["files", var filename]) => await PostFileAsync(filename, sb.ToString()),
            _ => notFound
        };

        Task<ArraySegment<byte>> OkAsync()
        {
            var result = headers.AcceptsGzip()
                ? (byte[]) [..ok, ..contentEncodingGzip, ..newLine]
                : [..ok, ..newLine];

            return Task.FromResult(new ArraySegment<byte>(result));
        }

        byte[] EchoAsync(string body)
        {
            if (!headers.AcceptsGzip())
                return Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n{body}");
            
            var data = body.GZip();
            
            return
            [
                ..Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Encoding: gzip\r\n\r\nContent-Length: {data.Length}\r\n\r\n"),
                ..body.GZip()
            ];

        }

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

        await socket.SendAsync(response);
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
        socket.Dispose();
    }
}

public static class Extensions
{
    private const string AcceptEncoding = "Accept-Encoding";

    public static bool AcceptsGzip(this IDictionary<string, string> headers)
    {
        return headers.TryGetValue(AcceptEncoding, out var value) && value.Split(", ").Contains("gzip");
    }

    public static byte[] GZip(this string self)
    {
        var bytes = Encoding.UTF8.GetBytes(self);
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        using var gZipStream = new GZipStream(output, CompressionMode.Compress);
        input.CopyTo(gZipStream);
        return output.ToArray();
    }
}