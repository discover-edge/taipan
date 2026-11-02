using System.ComponentModel;
using System.Diagnostics;

namespace Taipan.Cli;

/// <summary>Runs the .NET SDK's <c>dotnet</c>, for restoring packages (SPEC §13.2) and native builds (SPEC §15.1).</summary>
public static class DotNetSdk
{
    public sealed record Output(bool Success, string Text);

    // Runs the SDK's dotnet; the message if it can't be started.
    public static string? Run(string[] arguments, string directory, out Output output)
    {
        output = new(false, "");
        var start = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        // No build servers or reused nodes, which would outlive the command.
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        try
        {
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            output = new(process.ExitCode == 0, stdout + stderr.Result);
            return null;
        }
        catch (Win32Exception)
        {
            return "the .NET SDK was not found";
        }
    }

    /// <summary>The first error <c>dotnet</c> reported, or its first line of output.</summary>
    public static string FirstError(string output) =>
        Messages(output, "error").FirstOrDefault()
        ?? (output.Trim() is { Length: > 0 } text ? text.Split('\n')[0].Trim() : "dotnet failed");

    /// <summary>
    /// The messages of a kind (<c>error</c> or <c>warning</c>) <c>dotnet</c> reported, each once:
    /// <c>…/restore.csproj : error NU1101: Unable to find package X. … [/…/restore.csproj]</c> is
    /// <c>NU1101: Unable to find package X. …</c>.
    /// </summary>
    public static IEnumerable<string> Messages(string output, string kind)
    {
        var marker = $": {kind} ";
        var seen = new HashSet<string>();
        foreach (var line in output.Split('\n'))
        {
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                continue;
            // `collect2 : error : ld returned 1 exit status` has a second colon.
            var message = line[(at + marker.Length)..].TrimStart(':', ' ').Trim();
            var project = message.LastIndexOf(" [", StringComparison.Ordinal);
            message = project > 0 && message.EndsWith(']') ? message[..project] : message;
            if (seen.Add(message))
                yield return message;
        }
    }
}
