namespace Taipan.Runtime;

/// <summary>
/// Where compiled programs write. Defaults to the console; <c>taipan run</c> redirects these
/// while it executes a program in-process. Not thread-safe: one program runs at a time.
/// </summary>
public static class Io
{
    public static TextWriter Out { get; set; } = Console.Out;

    public static TextWriter Error { get; set; } = Console.Error;
}
