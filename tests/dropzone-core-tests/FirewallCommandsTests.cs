using Dropzone.Core.Firewall;

namespace Dropzone.Core.Tests;

public class FirewallCommandsTests
{
    private const string Exe = @"C:\Apps\LocalSendDropzone.exe";

    [Fact]
    public void ElevatedArguments_ParseBackToThePort()
    {
        string[] args = FirewallCommands.ElevatedArguments(53319).Split(' ');

        Assert.True(FirewallCommands.TryParseElevatedArguments(args, out int port));
        Assert.Equal(53319, port);
    }

    [Theory]
    [InlineData()]
    [InlineData("--background")]
    [InlineData("--add-firewall-rules")]
    [InlineData("--add-firewall-rules", "0")]
    [InlineData("--add-firewall-rules", "65536")]
    [InlineData("--add-firewall-rules", "+53319")]
    [InlineData("--add-firewall-rules", " 53319")]
    [InlineData("--add-firewall-rules", "port")]
    [InlineData("--ADD-FIREWALL-RULES", "53319")]
    [InlineData("--add-firewall-rules", "53319", "extra")]
    public void TryParseElevatedArguments_AnythingElse_False(params string[] args)
    {
        Assert.False(FirewallCommands.TryParseElevatedArguments(args, out _));
    }

    [Fact]
    public void NetshArguments_DeleteThenAddTcpAndUdp()
    {
        Assert.Equal(
            [
                "advfirewall firewall delete rule name=\"LocalSend Dropzone\" dir=in",
                "advfirewall firewall add rule name=\"LocalSend Dropzone\" dir=in action=allow enable=yes "
                + "profile=private,public program=\"C:\\Apps\\LocalSendDropzone.exe\" protocol=TCP localport=53319",
                "advfirewall firewall add rule name=\"LocalSend Dropzone\" dir=in action=allow enable=yes "
                + "profile=private,public program=\"C:\\Apps\\LocalSendDropzone.exe\" protocol=UDP localport=53317",
            ],
            FirewallCommands.NetshArguments(Exe, 53319));
    }

    [Theory]
    [InlineData(@"D:\R&D\LocalSendDropzone.exe")]
    [InlineData(@"D:\100% done\%PATH%\LocalSendDropzone.exe")]
    [InlineData(@"D:\a^b!c (x86)\LocalSendDropzone.exe")]
    public void NetshArguments_SpecialCharactersInThePath_PassedVerbatim(string exePath)
    {
        // netsh is started directly, not through cmd, so nothing in the path is interpreted.
        IReadOnlyList<string> commands = FirewallCommands.NetshArguments(exePath, 53319);

        Assert.All(commands.Skip(1), command => Assert.Contains($" program=\"{exePath}\" ", command));
    }

    [Fact]
    public void NetshArguments_QuoteInThePath_Throws()
    {
        Assert.Throws<ArgumentException>(() => FirewallCommands.NetshArguments("C:\\a\"b\\x.exe", 53319));
    }
}
