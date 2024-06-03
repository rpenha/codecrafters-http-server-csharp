using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cocona;

var cts = new CancellationTokenSource();
var ct = cts.Token;
var pendingRequests = new ConcurrentBag<Task>();

CoconaLiteApp.Run((string directory) =>
{
    Console.WriteLine($"Directory: {directory}");

    if (!Directory.Exists(directory))
        throw new DirectoryNotFoundException($"Directory '{directory}' not found");
    
    Environment.CurrentDirectory = directory;

    Task.Run(async () =>
    {
        var server = new TcpListener(IPAddress.Any, 4221);
        server.Start();

        while (!ct.IsCancellationRequested)
        {
            var socket = await server.AcceptSocketAsync(); // wait for client
            var task = Task.Run(async () => await HandleRequest(socket, ct), ct);
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
    var notFound = "HTTP/1.1 404 Not Found\r\n\r\n"u8.ToArray();
    var ok = "HTTP/1.1 200 OK\r\n\r\n"u8.ToArray();
    const int bufferSize = 1024;
    var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

    try
    {
        await socket.ReceiveAsync(buffer, cancellationToken);
        using var reader = new StringReader(Encoding.UTF8.GetString(buffer));
        var line = await reader.ReadLineAsync(cancellationToken);

        // Headers
        var headers = new Dictionary<string, string>();

        while (await reader.ReadLineAsync(cancellationToken) is { } headerValue)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) break;
            var kv = headerValue.Split(": ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            headers.Add(kv[0], kv[1]);
        }

        var url = line!.Split(' ')[1];
        var urlFragments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var response = urlFragments switch
        {
            [] => ok,
            ["echo", var msg] => Encoding.UTF8.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {msg.Length}\r\n\r\n{msg}"
            ),
            ["user-agent"] => Encoding.UTF8.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {headers["User-Agent"].Length}\r\n\r\n{headers["User-Agent"]}"
            ),
            ["files", var filename] => await GetFileAsync(filename),
            _ => notFound
        };

        async Task<ArraySegment<byte>> GetFileAsync(string filename)
        {
            var directory = Environment.CurrentDirectory;
            var path = Path.Combine(directory, filename);
            
            if (!File.Exists(path)) return notFound;
            
            var body = await File.ReadAllBytesAsync(path, cancellationToken);
            
            return new ArraySegment<byte>([
                ..Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\n\r\n"),
                ..body
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