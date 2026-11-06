namespace SteamStopper.Core;

// Stand-in for Core/Support.cs's Format, which drags in Windows-only Steam and firewall code.
public static class Format
{
    public static string Bytes(long num) => $"{num / 1048576.0:0.0} MB";
    public static string Speed(double bps) => $"{bps / 1048576.0:0.0} MB/s";
    public static string Duration(double? seconds) => $"{seconds:0}s";
}
