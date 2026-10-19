using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Dropzone.Core.Discovery;

/// <summary>Host addresses of small IPv4 subnets, for the register scan.</summary>
public static class SubnetMath
{
    /// <summary>The smallest prefix that is scanned; larger subnets (fewer prefix bits) are too big to sweep.</summary>
    public const int MinPrefixLength = 24;

    /// <summary>
    /// Every host of <paramref name="address"/>'s subnet except the network address, the broadcast address and
    /// <paramref name="address"/> itself, in ascending order. Empty for IPv6, for prefixes shorter than 24, and for
    /// /31 and /32.
    /// </summary>
    public static IEnumerable<IPAddress> Hosts(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || prefixLength is < MinPrefixLength or > 30)
        {
            yield break;
        }

        uint self = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        uint mask = uint.MaxValue << (32 - prefixLength);
        uint network = self & mask;
        uint broadcast = network | ~mask;
        for (uint host = network + 1; host < broadcast; host++)
        {
            if (host != self)
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, host);
                yield return new IPAddress(bytes);
            }
        }
    }
}
