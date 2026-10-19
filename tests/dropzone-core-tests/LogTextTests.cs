using Dropzone.Core.Logging;

namespace Dropzone.Core.Tests;

public class LogTextTests
{
    [Theory]
    [InlineData("Pixel 8", "Pixel 8")]
    [InlineData("报告 PC", "报告 PC")]
    [InlineData("evil\r\n2026-10-19 INFO forged", @"evil\r\n2026-10-19 INFO forged")]
    [InlineData("tab\there", @"tab\there")]
    [InlineData("bell\u0007", @"bell\u0007")]
    [InlineData("nel\u0085x", @"nel\u0085x")]
    [InlineData("line\u2028sep\u2029", @"line\u2028sep\u2029")]
    [InlineData("", "")]
    public void SingleLine_EscapesLineBreaksAndControlCharacters(string input, string expected)
    {
        Assert.Equal(expected, LogText.SingleLine(input));
    }

    [Fact]
    public void SingleLine_Null_IsEmpty()
    {
        Assert.Equal(string.Empty, LogText.SingleLine(null));
    }
}
