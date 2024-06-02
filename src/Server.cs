using System.Net;
using System.Net.Sockets;

var server = new TcpListener(IPAddress.Any, 4221);
server.Start();

using var socket  = server.AcceptSocket(); // wait for client
var response = "HTTP/1.1 200 OK\r\n\r\n"u8.ToArray();
await socket.SendAsync(response.ToArray());
