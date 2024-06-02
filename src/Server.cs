using System.Net;
using System.Net.Sockets;
using System.Text;

var cts = new CancellationTokenSource();
var ct = cts.Token;

var server = new TcpListener(IPAddress.Any, 4221);
server.Start();

var notFound = "HTTP/1.1 404 Not Found\r\n\r\n"u8.ToArray();
var ok = "HTTP/1.1 200 OK\r\n\r\n"u8.ToArray();

const int bufferSize = 1024;

using var socket  = server.AcceptSocket(); // wait for client
HashSet<string> urls = ["/", "index.html"];
var buffer = new byte[bufferSize];

await socket.ReceiveAsync(buffer, ct);

using var reader = new StringReader(Encoding.UTF8.GetString(buffer));

var line = await reader.ReadLineAsync(ct);

var url = line!.Split(' ')[1];

var response = urls.Contains(url) ? ok : notFound;

await socket.SendAsync(response);