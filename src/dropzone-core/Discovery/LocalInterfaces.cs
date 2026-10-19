using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Dropzone.Core.Discovery;

/// <summary>An IPv4 address of this PC and the prefix length of its subnet.</summary>
public readonly record struct LocalAddress(IPAddress Address, int PrefixLength);

/// <summary>This PC's addresses that discovery announces on and scans from.</summary>
public static class LocalInterfaces
{
    /// <summary>
    /// IPv4 unicast addresses of interfaces that are up and are neither loopback nor tunnel, excluding link-local
    /// (169.254.0.0/16) addresses. Empty when the interfaces cannot be read.
    /// </summary>
    public static IReadOnlyList<LocalAddress> Ipv4()
    {
        var result = new List<LocalAddress>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return result;
        }

        foreach (NetworkInterface nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
            {
                if (IsUsableAddress(unicast.Address))
                {
                    result.Add(new LocalAddress(unicast.Address, unicast.PrefixLength));
                }
            }
        }

        return result;
    }

    /// <summary>True for an IPv4 address that is neither loopback nor link-local (169.254.0.0/16).</summary>
    public static bool IsUsableAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        return !(bytes[0] == 169 && bytes[1] == 254);
    }
}
