using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Dropzone.Core.Firewall;

/// <summary>
/// One Windows Firewall rule as the COM API (<c>INetFwRule</c>) reports it. Numbers are the API's own values:
/// protocol 6 = TCP, 17 = UDP; direction 1 = in; action 0 = block, 1 = allow; profiles is a bit mask (2 = private,
/// 4 = public).
/// </summary>
public sealed record FirewallRuleInfo(
    string? Name,
    string? ApplicationName,
    int Protocol,
    string? LocalPorts,
    int Profiles,
    int Direction,
    bool Enabled,
    int Action);

/// <summary>Decides whether this program's inbound firewall rules are in place.</summary>
public static class FirewallRuleCheck
{
    public const string RuleName = "LocalSend Dropzone";
    public const int MulticastPort = 53317;
    public const int ProtocolTcp = 6;
    public const int ProtocolUdp = 17;
    public const int ProfilePrivate = 2;
    public const int ProfilePublic = 4;

    /// <summary>NET_FW_IP_PROTOCOL_ANY.</summary>
    public const int ProtocolAny = 256;

    private const int DirectionIn = 1;
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;

    /// <summary>
    /// True when enabled inbound allow rules for <paramref name="exePath"/> (case-insensitive) let in TCP
    /// <paramref name="privatePort"/> and UDP 53317, each covering both the private and the public profile, and no
    /// block rule stops them (<see cref="Blocks"/>; Windows lets a block rule win over any allow rule). Any rule
    /// counts, whatever its name, including ones with LocalPorts "*" (such as those Windows' own "allow access"
    /// prompt creates), so this program's own rules are only added when nothing else already covers it.
    /// </summary>
    public static bool Covers(IEnumerable<FirewallRuleInfo> rules, string exePath, int privatePort)
    {
        List<FirewallRuleInfo> forExe = rules.Where(r => IsInboundFor(r, exePath)).ToList();
        if (Blocks(forExe, exePath, privatePort))
        {
            return false;
        }

        List<FirewallRuleInfo> allowing = forExe
            .Where(r => r.Action == ActionAllow
                        && (r.Profiles & (ProfilePrivate | ProfilePublic)) == (ProfilePrivate | ProfilePublic))
            .ToList();
        return allowing.Any(r => HasProtocol(r, ProtocolTcp) && ListsPort(r.LocalPorts, privatePort, emptyMeansAll: false))
               && allowing.Any(r => HasProtocol(r, ProtocolUdp) && ListsPort(r.LocalPorts, MulticastPort, emptyMeansAll: false));
    }

    /// <summary>
    /// True when an enabled inbound block rule for <paramref name="exePath"/>, whatever its name, applies on the
    /// private or the public profile to TCP <paramref name="privatePort"/> or UDP 53317 (including "*", ranges and
    /// "any protocol"). Such a rule usually comes from Windows' own firewall prompt answered without ticking a network
    /// type. Adding allow rules cannot override it; the user has to allow the program in Windows' firewall settings.
    /// </summary>
    public static bool Blocks(IEnumerable<FirewallRuleInfo> rules, string exePath, int privatePort) =>
        rules.Any(r => IsInboundFor(r, exePath)
                       && r.Action == ActionBlock
                       && (r.Profiles & (ProfilePrivate | ProfilePublic)) != 0
                       && ((HasProtocol(r, ProtocolTcp) && ListsPort(r.LocalPorts, privatePort, emptyMeansAll: true))
                           || (HasProtocol(r, ProtocolUdp) && ListsPort(r.LocalPorts, MulticastPort, emptyMeansAll: true))));

    private static bool HasProtocol(FirewallRuleInfo rule, int protocol) =>
        rule.Protocol == protocol || rule.Protocol == ProtocolAny;

    private static bool IsInboundFor(FirewallRuleInfo rule, string exePath) =>
        rule.Enabled && rule.Direction == DirectionIn && SamePath(rule.ApplicationName, exePath);

    /// <summary>
    /// True when <paramref name="localPorts"/> ("*", a number, a range "a-b", or a comma-separated list of those)
    /// includes <paramref name="port"/>. A rule without ports (any protocol) applies to every port; for an allow rule
    /// that is only counted when <paramref name="emptyMeansAll"/>, which errs towards asking for our own rules.
    /// </summary>
    private static bool ListsPort(string? localPorts, int port, bool emptyMeansAll)
    {
        if (string.IsNullOrWhiteSpace(localPorts))
        {
            return emptyMeansAll;
        }

        foreach (string part in localPorts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "*")
            {
                return true;
            }

            int dash = part.IndexOf('-');
            if (dash > 0
                && int.TryParse(part[..dash].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int low)
                && int.TryParse(part[(dash + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int high))
            {
                if (port >= low && port <= high)
                {
                    return true;
                }
            }
            else if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int single) && single == port)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SamePath(string? ruleApplication, string exePath)
    {
        if (string.IsNullOrEmpty(ruleApplication))
        {
            return false;
        }

        return string.Equals(Normalize(ruleApplication), Normalize(exePath), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        string expanded = Environment.ExpandEnvironmentVariables(path);
        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return expanded;
        }
    }
}
