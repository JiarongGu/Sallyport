using System.Net;
using System.Net.Sockets;
using System.Text;

// udpsend <ipv4> <port> [count]: one datagram a second, each naming the program's own path, so a log that sees it
// can tell which copy sent it.
if (args.Length < 2 || !IPAddress.TryParse(args[0], out var address))
{
    Console.Error.WriteLine("usage: udpsend <ipv4> <port> [count]");
    return 2;
}
var count = args.Length >= 3 && int.TryParse(args[2], out var c) ? c : 5;
using var socket = new UdpClient(AddressFamily.InterNetwork);
var payload = Encoding.UTF8.GetBytes(Environment.ProcessPath ?? "udpsend");
for (var i = 0; i < count; i++)
{
    socket.Send(payload, new IPEndPoint(address, int.Parse(args[1])));
    Console.WriteLine($"sent {payload.Length} B to {address}:{args[1]} from local port {((IPEndPoint)socket.Client.LocalEndPoint!).Port}");
    Thread.Sleep(1000);
}
return 0;
