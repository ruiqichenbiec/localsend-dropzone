using System.Net;
using System.Net.Sockets;

namespace Dropzone.Core.Tests.TestSupport;

/// <summary>Finds a loopback TCP port that is free right now.</summary>
internal static class FreePort
{
    public static int Get()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
