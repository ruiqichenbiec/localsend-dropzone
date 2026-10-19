using System.Text;

namespace Dropzone.Core.Settings;

/// <summary>Screen corner of the send area, inside the monitor's work area.</summary>
public enum Corner
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
}

/// <summary>This program's settings. <see cref="object.ToString"/> masks the password.</summary>
/// <param name="Password">Shared password; null, empty or whitespace means "no password set" (private channel off).</param>
/// <param name="PrivatePort">TCP port of the dedicated receive server; the same on every PC.</param>
/// <param name="Corner">Where the send area appears.</param>
/// <param name="AutoStart">Start with Windows.</param>
/// <param name="FirstRunDone">True once the first-run checks have run.</param>
/// <param name="OriginalAutoStart">
/// Whether the official app started with Windows at the last check; null before the first one. The hint to turn it on
/// is shown only when this changes to false.
/// </param>
public record AppSettings(
    string? Password,
    int PrivatePort = 53319,
    Corner Corner = Corner.BottomRight,
    bool AutoStart = true,
    bool FirstRunDone = false,
    bool? OriginalAutoStart = null)
{
    /// <summary>Like the generated one, but the password is masked so it can never reach a log.</summary>
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Password = ").Append(Password is null ? "null" : "***");
        builder.Append(", PrivatePort = ").Append(PrivatePort);
        builder.Append(", Corner = ").Append(Corner);
        builder.Append(", AutoStart = ").Append(AutoStart);
        builder.Append(", FirstRunDone = ").Append(FirstRunDone);
        builder.Append(", OriginalAutoStart = ").Append(OriginalAutoStart?.ToString() ?? "null");
        return true;
    }
}
