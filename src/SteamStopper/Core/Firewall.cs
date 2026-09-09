using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamStopper.Core;

public static class Firewall
{
    public const string Prefix = "SteamStopper";

    public static List<string> RuleNames()
    {
        var psi = new ProcessStartInfo("powershell")
        {
            Arguments = "-NoProfile -Command \"Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '" + Prefix + "*' } | Select-Object -ExpandProperty DisplayName\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc is null) return [];
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();
    }

    public static bool IsBlocked() => RuleNames().Count > 0;

    public static int Block(string steamRoot, bool includeGames)
    {
        Unblock();
        var programs = SteamClient.ClientExePaths(steamRoot).ToList();
        if (includeGames) programs.AddRange(SteamClient.GameExePaths(steamRoot));
        var unique = programs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var program in unique)
        {
            AddRule(program, "out");
            AddRule(program, "in");
        }
        return unique.Count;
    }

    public static int Unblock()
    {
        var names = RuleNames();
        foreach (var name in names)
            RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
        return names.Count;
    }

    private static void AddRule(string program, string direction)
    {
        var safe = Regex.Replace(Path.GetFileName(program), @"[^A-Za-z0-9._-]+", "_");
        var digest = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(program.ToLowerInvariant())))[..10];
        var name = $"{Prefix}-{direction}-{safe}-{digest}";
        RunNetsh($"advfirewall firewall add rule name=\"{name}\" dir={direction} action=block program=\"{program}\" enable=yes profile=any");
    }

    private static void RunNetsh(string args)
    {
        using var proc = Process.Start(new ProcessStartInfo("netsh", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        proc?.WaitForExit();
    }
}
