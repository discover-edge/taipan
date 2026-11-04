using System.Runtime.InteropServices;
using System.Text;

namespace Taipan.Runtime;

/// <summary>
/// What a native library's C entry points use (SPEC §15.6, M45): its one-time initialization,
/// UTF-8 strings in and out, and each thread's last error.
/// </summary>
public static unsafe class NativeExports
{
    private static readonly object Gate = new();
    private static bool _initialized;

    // The calling thread's last error, as C reads it: a UTF-8 string the library owns, or 0.
    [ThreadStatic]
    private static nint t_lastError;

    /// <summary>Starts a call: the module initializes on the first one (SPEC §14.3), and the thread's last error is cleared.</summary>
    public static void Begin(Action initialize)
    {
        Clear();
        if (_initialized)
            return;
        lock (Gate)
        {
            if (_initialized)
                return;
            initialize();
            _initialized = true;
        }
    }

    /// <summary>Keeps what a call raised as the thread's last error: a traceback's last line, <c>ValueError: …</c>.</summary>
    public static void Fail(Exception exception)
    {
        Io.Out.Flush();
        var text = exception is TaipanPanic panic ? $"panic: {panic.Message}"
            : Tracebacks.Format(Exceptions.From(exception)).TrimEnd('\n').Split('\n')[^1];
        Clear();
        t_lastError = Allocate(text);
    }

    public static nint LastError() => t_lastError;

    private static void Clear()
    {
        if (t_lastError != 0)
            NativeMemory.Free((void*)t_lastError);
        t_lastError = 0;
    }

    /// <summary>A C string argument, UTF-8 and null-terminated.</summary>
    public static string Str(nint text) => text == 0 ? "" : Marshal.PtrToStringUTF8(text) ?? "";

    /// <summary>A string result, which the caller frees with <c>&lt;name&gt;_free</c>.</summary>
    public static nint FromStr(string text) => Allocate(text);

    public static void Free(nint text)
    {
        if (text != 0)
            NativeMemory.Free((void*)text);
    }

    private static nint Allocate(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var memory = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(memory, bytes.Length));
        memory[bytes.Length] = 0;
        return (nint)memory;
    }
}
