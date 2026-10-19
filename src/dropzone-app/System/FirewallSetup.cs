using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dropzone.Core.Firewall;

namespace Dropzone.App.SystemIntegration;

public enum FirewallResult
{
    /// <summary>Both rules already exist for this exe and port, on the private and public profiles.</summary>
    AlreadyPresent,

    /// <summary>The rules were (re)created and are now present.</summary>
    Added,

    /// <summary>The user refused the elevation prompt (ERROR_CANCELLED soon after it appeared).</summary>
    Declined,

    /// <summary>
    /// The elevation prompt was left unanswered until Windows dismissed it. Windows reports this with the same
    /// ERROR_CANCELLED as a refusal; it is told apart by how long the prompt was up.
    /// </summary>
    TimedOut,

    /// <summary>
    /// The elevated instance ran (or could not run, or did not finish within 60 seconds) but the rules are still missing.
    /// </summary>
    Failed,

    /// <summary>
    /// An enabled inbound block rule names this exe (usually left by Windows' own firewall prompt). Allow rules
    /// cannot override it, so no elevation was asked for; the user has to allow the program in Windows' settings.
    /// </summary>
    Blocked,
}

/// <summary>
/// Makes sure inbound TCP on the dedicated port and UDP 53317 are allowed for this exe on the private and public
/// profiles (like the official app). Any existing rule counts, whatever its name (Windows' own "allow access" prompt
/// creates suitable ones); rules are read through the Windows Firewall COM API (no elevation, no localized text).
/// Only when nothing covers it are this program's own rules, named <c>LocalSend Dropzone</c>, replaced: this exe is
/// started once more, elevated, with <see cref="FirewallCommands.ElevatedArguments"/>, and that instance runs netsh
/// directly (<see cref="RunElevated"/>; no <c>cmd</c>, so <c>&amp;</c> or <c>%</c> in the exe path cannot break the
/// command). A block rule for this exe is reported, never removed: rules with any other name are never deleted, and
/// the network category is never touched.
/// </summary>
public static class FirewallSetup
{
    private const int ErrorCancelled = 1223;

    /// <summary>A cancel this long after the prompt appeared is taken as Windows dismissing an unanswered prompt.</summary>
    private static readonly TimeSpan PromptTimeoutGuess = TimeSpan.FromSeconds(100);

    /// <summary>How long the elevated instance may run once the prompt was accepted.</summary>
    private static readonly TimeSpan ElevatedTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long one netsh command may run inside the elevated instance; three fit in <see cref="ElevatedTimeout"/>.</summary>
    private static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(15);

    private static readonly SemaphoreSlim Serial = new(1, 1);

    /// <summary>
    /// Checks the rules and, when they are missing or name another exe, port or profile set, replaces them (a UAC
    /// prompt). Success is decided by reading the rules again, not by the command's exit code. When a block rule for
    /// this exe applies, returns <see cref="FirewallResult.Blocked"/> without a prompt. Calls are serialized.
    /// </summary>
    public static async Task<FirewallResult> EnsureAsync(string exePath, int privatePort)
    {
        await Serial.WaitAsync().ConfigureAwait(false);
        try
        {
            switch (Check(exePath, privatePort))
            {
                case RuleState.Covered:
                    return FirewallResult.AlreadyPresent;
                case RuleState.Blocked:
                    return FirewallResult.Blocked;
            }

            ProcessStartInfo elevated = new(exePath, FirewallCommands.ElevatedArguments(privatePort))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Stopwatch prompt = Stopwatch.StartNew();
            try
            {
                using Process? process = Process.Start(elevated);
                if (process is not null)
                {
                    // Process.Start returned, so the prompt was accepted; from here on the wait is capped. The elevated
                    // instance cannot be killed from here (it runs at a higher integrity level) and is left to finish.
                    using CancellationTokenSource deadline = new(ElevatedTimeout);
                    try
                    {
                        await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                        AppLog.Info($"The elevated firewall setup exited with code {ExitCodeText(process)}.");
                    }
                    catch (OperationCanceledException)
                    {
                        AppLog.Warn($"The elevated firewall setup did not finish within {ElevatedTimeout.TotalSeconds:0} s.");
                        return FirewallResult.Failed;
                    }
                }
            }
            catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
            {
                // This code has no timeout of its own: Process.Start returns only when the UAC prompt is answered or
                // Windows dismisses it.
                bool timedOut = prompt.Elapsed >= PromptTimeoutGuess;
                AppLog.Info($"UAC prompt for the firewall rules ended with ERROR_CANCELLED after {prompt.Elapsed.TotalSeconds:0} s: "
                            + (timedOut ? "Timed out (left unanswered)." : "Cancelled by the user."));
                return timedOut ? FirewallResult.TimedOut : FirewallResult.Declined;
            }
            catch (Win32Exception e)
            {
                AppLog.Warn("Could not start the elevated firewall setup.", e);
                return FirewallResult.Failed;
            }

            return Check(exePath, privatePort) switch
            {
                RuleState.Covered => FirewallResult.Added,
                RuleState.Blocked => FirewallResult.Blocked,
                _ => FirewallResult.Failed,
            };
        }
        finally
        {
            Serial.Release();
        }
    }

    /// <summary>
    /// Reads the current rules once: blocked for this exe, covering TCP <paramref name="privatePort"/> and UDP 53317,
    /// or neither (also when the rules cannot be read).
    /// </summary>
    private static RuleState Check(string exePath, int privatePort)
    {
        try
        {
            List<FirewallRuleInfo> rules = ReadRules();
            if (FirewallRuleCheck.Blocks(rules, exePath, privatePort))
            {
                return RuleState.Blocked;
            }

            return FirewallRuleCheck.Covers(rules, exePath, privatePort) ? RuleState.Covered : RuleState.Missing;
        }
        catch (Exception e) when (e is COMException or InvalidCastException
                                      or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            AppLog.Warn("Could not read the firewall rules.", e);
            return RuleState.Missing;
        }
    }

    private enum RuleState
    {
        Missing,
        Covered,
        Blocked,
    }

    /// <summary>Every rule that names a program, read through <c>HNetCfg.FwPolicy2</c>.</summary>
    private static List<FirewallRuleInfo> ReadRules()
    {
        Type policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
        dynamic policy = Activator.CreateInstance(policyType)!;
        List<FirewallRuleInfo> rules = [];
        try
        {
            dynamic all = policy.Rules;
            try
            {
                foreach (dynamic rule in (IEnumerable)all)
                {
                    try
                    {
                        // Any rule may cover this exe, whatever its name; rules without a program cannot.
                        string? application = rule.ApplicationName;
                        if (!string.IsNullOrEmpty(application))
                        {
                            rules.Add(new FirewallRuleInfo(
                                (string?)rule.Name,
                                application,
                                (int)rule.Protocol,
                                (string?)rule.LocalPorts,
                                (int)rule.Profiles,
                                (int)rule.Direction,
                                (bool)rule.Enabled,
                                (int)rule.Action));
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(rule);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(all);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }

        return rules;
    }

    /// <summary>
    /// The elevated instance's whole job: deletes any old inbound rules of this program's name, then adds the two
    /// current ones for <paramref name="exePath"/> (this exe), running System32's netsh once per command without a
    /// shell. A netsh that hangs is killed after 15 seconds. Returns the process exit code: 0 when both adds succeeded
    /// (the delete finds nothing on a first run). The caller decides success by reading the rules again anyway.
    /// </summary>
    public static int RunElevated(string exePath, int privatePort)
    {
        string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
        IReadOnlyList<string> commands = FirewallCommands.NetshArguments(exePath, privatePort);
        bool added = true;
        for (int i = 0; i < commands.Count; i++)
        {
            int? exitCode = RunHidden(netsh, commands[i]);
            if (i > 0 && exitCode != 0)
            {
                added = false;
            }
        }

        return added ? 0 : 1;
    }

    /// <summary>The exit code for the log, or "unknown" when the elevated process's handle does not allow reading it.</summary>
    private static string ExitCodeText(Process process)
    {
        try
        {
            return process.ExitCode.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            return "unknown";
        }
    }

    /// <summary>Runs <paramref name="fileName"/> without a window or shell; null when it could not start or hung.</summary>
    private static int? RunHidden(string fileName, string arguments)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            if (process.WaitForExit(NetshTimeout))
            {
                return process.ExitCode;
            }

            process.Kill();
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
