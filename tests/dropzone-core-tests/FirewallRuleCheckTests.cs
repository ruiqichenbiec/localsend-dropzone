using Dropzone.Core.Firewall;

namespace Dropzone.Core.Tests;

public class FirewallRuleCheckTests
{
    private const string Exe = @"C:\Tools\localsend-dropzone\publish\LocalSendDropzone.exe";

    private static FirewallRuleInfo Rule(
        int protocol,
        string ports,
        int profiles = 6,
        string name = FirewallRuleCheck.RuleName,
        string app = Exe,
        bool enabled = true,
        int direction = 1,
        int action = 1) =>
        new(name, app, protocol, ports, profiles, direction, enabled, action);

    private static FirewallRuleInfo[] BothRules(int profiles = 6) =>
        [Rule(FirewallRuleCheck.ProtocolTcp, "53319", profiles), Rule(FirewallRuleCheck.ProtocolUdp, "53317", profiles)];

    [Fact]
    public void Covers_BothRulesOnPrivateAndPublic_True()
    {
        Assert.True(FirewallRuleCheck.Covers(BothRules(), Exe, 53319));
    }

    [Fact]
    public void Covers_AllProfilesMask_True()
    {
        Assert.True(FirewallRuleCheck.Covers(BothRules(0x7FFFFFFF), Exe, 53319));
    }

    [Fact]
    public void Covers_PrivateOnlyRules_False()
    {
        // What the first version created: Profiles = 2.
        Assert.False(FirewallRuleCheck.Covers(BothRules(2), Exe, 53319));
    }

    [Fact]
    public void Covers_PathDiffersOnlyInCase_True()
    {
        FirewallRuleInfo[] rules =
        [
            Rule(FirewallRuleCheck.ProtocolTcp, "53319", app: Exe.ToLowerInvariant()),
            Rule(FirewallRuleCheck.ProtocolUdp, "53317", app: Exe.ToLowerInvariant()),
        ];

        Assert.True(FirewallRuleCheck.Covers(rules, Exe, 53319));
    }

    [Fact]
    public void Covers_OtherExe_False()
    {
        FirewallRuleInfo[] rules =
        [
            Rule(FirewallRuleCheck.ProtocolTcp, "53319", app: @"D:\old\LocalSendDropzone.exe"),
            Rule(FirewallRuleCheck.ProtocolUdp, "53317", app: @"D:\old\LocalSendDropzone.exe"),
        ];

        Assert.False(FirewallRuleCheck.Covers(rules, Exe, 53319));
    }

    [Fact]
    public void Covers_OtherPort_False()
    {
        Assert.False(FirewallRuleCheck.Covers(BothRules(), Exe, 54000));
    }

    [Fact]
    public void Covers_PortInList_True()
    {
        FirewallRuleInfo[] rules =
        [
            Rule(FirewallRuleCheck.ProtocolTcp, "1000, 53319"),
            Rule(FirewallRuleCheck.ProtocolUdp, "53317"),
        ];

        Assert.True(FirewallRuleCheck.Covers(rules, Exe, 53319));
    }

    [Fact]
    public void Covers_OnlyTcpRule_False()
    {
        Assert.False(FirewallRuleCheck.Covers([Rule(FirewallRuleCheck.ProtocolTcp, "53319")], Exe, 53319));
    }

    [Fact]
    public void Covers_WindowsPromptRulesAnyPortAnotherName_True()
    {
        // What Windows' own "allow access" prompt created on this PC: named after the exe, lower-cased path,
        // LocalPorts "*", private + public.
        FirewallRuleInfo[] rules =
        [
            Rule(FirewallRuleCheck.ProtocolTcp, "*", name: "LocalSendDropzone", app: Exe.ToLowerInvariant()),
            Rule(FirewallRuleCheck.ProtocolUdp, "*", name: "LocalSendDropzone", app: Exe.ToLowerInvariant()),
        ];

        Assert.True(FirewallRuleCheck.Covers(rules, Exe, 53319));
    }

    [Fact]
    public void Covers_MixOfOurRuleAndAnotherRule_True()
    {
        FirewallRuleInfo[] rules =
        [
            Rule(FirewallRuleCheck.ProtocolTcp, "53319"),
            Rule(FirewallRuleCheck.ProtocolUdp, "*", name: "LocalSendDropzone"),
        ];

        Assert.True(FirewallRuleCheck.Covers(rules, Exe, 53319));
    }

    [Fact]
    public void Covers_AnyProtocolAnyPortRule_True()
    {
        Assert.True(FirewallRuleCheck.Covers(
            [Rule(FirewallRuleCheck.ProtocolAny, "*", name: "Something else")], Exe, 53319));
    }

    [Fact]
    public void Covers_AnyPortRulesForAnotherExe_False()
    {
        FirewallRuleInfo[] rules =
        [
            Rule(FirewallRuleCheck.ProtocolTcp, "*", name: "LocalSend", app: @"C:\Program Files\LocalSend\localsend_app.exe"),
            Rule(FirewallRuleCheck.ProtocolUdp, "*", name: "LocalSend", app: @"C:\Program Files\LocalSend\localsend_app.exe"),
        ];

        Assert.False(FirewallRuleCheck.Covers(rules, Exe, 53319));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void Covers_AnyPortRuleWithAnotherNameDisabledOrBlock_False(bool enabled, int action)
    {
        FirewallRuleInfo[] rules =
        [
            Rule(FirewallRuleCheck.ProtocolTcp, "*", name: "LocalSendDropzone", enabled: enabled, action: action),
            Rule(FirewallRuleCheck.ProtocolUdp, "*", name: "LocalSendDropzone", enabled: enabled, action: action),
        ];

        Assert.False(FirewallRuleCheck.Covers(rules, Exe, 53319));
    }

    [Fact]
    public void Covers_AnyPortRuleWithAnotherNamePrivateOnly_False()
    {
        FirewallRuleInfo[] rules =
        [
            Rule(FirewallRuleCheck.ProtocolTcp, "*", profiles: 2, name: "LocalSendDropzone"),
            Rule(FirewallRuleCheck.ProtocolUdp, "*", profiles: 2, name: "LocalSendDropzone"),
        ];

        Assert.False(FirewallRuleCheck.Covers(rules, Exe, 53319));
    }

    [Fact]
    public void Covers_PortRange_True()
    {
        FirewallRuleInfo[] rules =
        [
            Rule(FirewallRuleCheck.ProtocolTcp, "53300-53320"),
            Rule(FirewallRuleCheck.ProtocolUdp, "53317"),
        ];

        Assert.True(FirewallRuleCheck.Covers(rules, Exe, 53319));
    }

    // ---------------------------------------------------------------- block rules

    [Theory]
    [InlineData(FirewallRuleCheck.ProtocolTcp, "53319", 2)]
    [InlineData(FirewallRuleCheck.ProtocolTcp, "*", 4)]
    [InlineData(FirewallRuleCheck.ProtocolUdp, "53317", 4)]
    [InlineData(FirewallRuleCheck.ProtocolUdp, "*", 2)]
    [InlineData(FirewallRuleCheck.ProtocolAny, "*", 0x7FFFFFFF)]
    [InlineData(FirewallRuleCheck.ProtocolTcp, "53300-53320", 6)]
    public void Blocks_EnabledInboundBlockForThisExe_True(int protocol, string ports, int profiles)
    {
        // What Windows' own prompt creates when "allow" is not ticked: a block rule named after the exe.
        FirewallRuleInfo[] rules =
        [
            .. BothRules(),
            Rule(protocol, ports, profiles, name: "localsenddropzone.exe", app: Exe.ToLowerInvariant(), action: 0),
        ];

        Assert.True(FirewallRuleCheck.Blocks(rules, Exe, 53319));
        Assert.False(FirewallRuleCheck.Covers(rules, Exe, 53319));
    }

    [Fact]
    public void Blocks_AnyProtocolWithoutPorts_True()
    {
        FirewallRuleInfo[] rules =
            [new("blocked", Exe, FirewallRuleCheck.ProtocolAny, null, 6, 1, true, 0)];

        Assert.True(FirewallRuleCheck.Blocks(rules, Exe, 53319));
    }

    [Theory]
    [InlineData(false, 1, 6, FirewallRuleCheck.ProtocolTcp, "53319")] // disabled
    [InlineData(true, 2, 6, FirewallRuleCheck.ProtocolTcp, "53319")] // outbound
    [InlineData(true, 1, 1, FirewallRuleCheck.ProtocolTcp, "53319")] // domain profile only
    [InlineData(true, 1, 6, FirewallRuleCheck.ProtocolTcp, "80")] // another port
    [InlineData(true, 1, 6, FirewallRuleCheck.ProtocolUdp, "53319")] // UDP on the private port
    [InlineData(true, 1, 6, FirewallRuleCheck.ProtocolTcp, "53317")] // TCP on the multicast port
    public void Blocks_RulesThatDoNotApply_False(bool enabled, int direction, int profiles, int protocol, string ports)
    {
        FirewallRuleInfo[] rules =
        [
            .. BothRules(),
            Rule(protocol, ports, profiles, name: "something", enabled: enabled, direction: direction, action: 0),
        ];

        Assert.False(FirewallRuleCheck.Blocks(rules, Exe, 53319));
        Assert.True(FirewallRuleCheck.Covers(rules, Exe, 53319));
    }

    [Fact]
    public void Blocks_BlockRuleForAnotherExe_False()
    {
        FirewallRuleInfo[] rules =
            [.. BothRules(), Rule(FirewallRuleCheck.ProtocolAny, "*", app: @"C:\Other\other.exe", action: 0)];

        Assert.False(FirewallRuleCheck.Blocks(rules, Exe, 53319));
        Assert.True(FirewallRuleCheck.Covers(rules, Exe, 53319));
    }

    [Theory]
    [InlineData(false, 1, 1)]
    [InlineData(true, 2, 1)]
    [InlineData(true, 1, 0)]
    public void Covers_DisabledOutboundOrBlock_False(bool enabled, int direction, int action)
    {
        FirewallRuleInfo[] rules =
        [
            Rule(FirewallRuleCheck.ProtocolTcp, "53319", enabled: enabled, direction: direction, action: action),
            Rule(FirewallRuleCheck.ProtocolUdp, "53317"),
        ];

        Assert.False(FirewallRuleCheck.Covers(rules, Exe, 53319));
    }
}
