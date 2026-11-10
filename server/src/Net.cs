using System.Net;
using System.Net.Sockets;

namespace Pickyfin.Server;

/// <summary>
/// One HTTP handler for all outgoing calls. Connects over IPv4 first: in many Docker
/// networks a name resolves to an IPv6 address the container can't reach, and the
/// connection then hangs instead of failing. A connect timeout makes any remaining
/// network problem show up within half a minute instead of many minutes. This is
/// only about opening the connection; waiting for the answer has its own, long limit.
/// </summary>
public static class Net
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectCallback = ConnectIpv4FirstAsync,
    };

    private static async ValueTask<Stream> ConnectIpv4FirstAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var ordered = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
        Exception? last = null;
        foreach (var address in ordered)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var perAddress = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                perAddress.CancelAfter(TimeSpan.FromSeconds(20));
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), perAddress.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                last = error;
            }
        }
        throw new HttpRequestException($"Could not connect to {context.DnsEndPoint.Host}: {last?.Message ?? "no addresses"}", last);
    }
}
