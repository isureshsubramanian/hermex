using System.Net;
using System.Net.Sockets;

namespace Hermex.Smtp;

/// <summary>Binds a TCP listener, transparently resolving port conflicts when permitted.</summary>
internal static class PortAllocator
{
    /// <summary>
    /// Starts a <see cref="TcpListener"/> on <paramref name="desiredPort"/>. When
    /// <paramref name="autoResolve"/> is enabled, consecutive ports are probed until a free
    /// one is found.
    /// </summary>
    /// <exception cref="HermexPortConflictException">No usable port could be bound.</exception>
    public static TcpListener Bind(IPAddress address, int desiredPort, bool autoResolve, int maxAttempts,
        out int boundPort, out IReadOnlyList<int> probedPorts)
    {
        var probed = new List<int>();
        var attempts = autoResolve ? Math.Max(1, maxAttempts) : 1;
        Exception? lastError = null;

        for (var i = 0; i < attempts; i++)
        {
            var port = desiredPort + i;
            if (port > 65535)
                break;

            probed.Add(port);
            var listener = new TcpListener(address, port);
            try
            {
                listener.Start();
                boundPort = port;
                probedPorts = probed;
                return listener;
            }
            catch (SocketException ex)
            {
                lastError = ex;
                try { listener.Stop(); }
                catch { /* ignore */ }
            }
        }

        probedPorts = probed;
        throw new HermexPortConflictException(desiredPort, probed, lastError);
    }
}
