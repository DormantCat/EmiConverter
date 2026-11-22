using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace EmiConverter;

internal static class LinuxTrace
{
    [DllImport("emi_trace", EntryPoint = "emi_hop")]
    private static extern int Hop(byte[] address, int size, int ttl, [Out] StringBuilder peer, int capacity);

    internal static Task<string> RunAsync(IPAddress ip, CancellationToken token) => Task.Run(() =>
    {
        var rows = new List<string>();
        var bytes = ip.GetAddressBytes();
        for (int ttl = 1; ttl <= 20; ttl++)
        {
            token.ThrowIfCancellationRequested();
            var peer = new StringBuilder(64);
            int status = Hop(bytes, bytes.Length, ttl, peer, peer.Capacity);
            if (status < 0) throw new IOException("Системный сокет трассировки недоступен.");
            rows.Add($"{ttl,2}   {(peer.Length == 0 ? "*" : peer.ToString())}");
            if (status == 1) break;
        }
        return string.Join(Environment.NewLine, rows);
    }, token);
}
