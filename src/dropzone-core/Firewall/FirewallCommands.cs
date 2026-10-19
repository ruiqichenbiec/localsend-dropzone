using System;
using System.Collections.Generic;
using System.Globalization;

namespace Dropzone.Core.Firewall;

/// <summary>
/// How this program's own firewall rules are written. The tray app starts its own exe elevated with
/// <see cref="ElevatedArguments"/>; that instance runs <c>netsh</c> once per command in <see cref="NetshArguments"/>,
/// directly rather than through <c>cmd</c>, so characters such as <c>&amp;</c> and <c>%</c> in the exe path reach
/// netsh as they are. Only the port crosses the elevation boundary; the elevated instance names itself as the program.
/// </summary>
public static class FirewallCommands
{
    /// <summary>First argument of the elevated instance; the second is the private port.</summary>
    public const string ElevatedSwitch = "--add-firewall-rules";

    /// <summary>Command line for the elevated instance of this exe.</summary>
    public static string ElevatedArguments(int privatePort) =>
        ElevatedSwitch + " " + privatePort.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// True when <paramref name="args"/> is exactly <see cref="ElevatedSwitch"/> followed by a port from 1 to 65535
    /// written as plain digits.
    /// </summary>
    public static bool TryParseElevatedArguments(IReadOnlyList<string> args, out int privatePort)
    {
        privatePort = 0;
        return args.Count == 2
               && args[0] == ElevatedSwitch
               && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out privatePort)
               && privatePort is >= 1 and <= 65535;
    }

    /// <summary>
    /// The netsh command lines, in order: delete every inbound rule named <see cref="FirewallRuleCheck.RuleName"/>,
    /// then add TCP <paramref name="privatePort"/> and UDP 53317 for <paramref name="exePath"/> on the private and
    /// public profiles. Meant for starting netsh without a shell. Throws when the path contains a double quote (no
    /// Windows path can).
    /// </summary>
    public static IReadOnlyList<string> NetshArguments(string exePath, int privatePort)
    {
        if (exePath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("The exe path contains a double quote.", nameof(exePath));
        }

        string name = FirewallRuleCheck.RuleName;
        string add = $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow enable=yes "
                     + $"profile=private,public program=\"{exePath}\"";
        return
        [
            $"advfirewall firewall delete rule name=\"{name}\" dir=in",
            $"{add} protocol=TCP localport={privatePort.ToString(CultureInfo.InvariantCulture)}",
            $"{add} protocol=UDP localport={FirewallRuleCheck.MulticastPort.ToString(CultureInfo.InvariantCulture)}",
        ];
    }
}
