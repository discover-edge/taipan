using System.Collections;
using System.Text;

namespace Taipan.Runtime;

/// <summary>
/// <c>TextIO</c> (SPEC §6.1, M37): a text file from <c>open</c>, or a standard stream. It's an
/// iterator over its remaining lines, as Python's file objects are.
/// </summary>
public sealed class TextFile : IIterator<string>
{
    // Files not yet closed, which the end of the program flushes and closes (SPEC §6.1).
    private static readonly HashSet<TextFile> Open = [];

    private readonly TextReader? _reader;
    private readonly TextWriter? _writer;
    private readonly Stream? _stream;
    // `newline=None`: universal newlines when reading, the platform's line ending when writing.
    private readonly bool _translate;
    private readonly bool _translateWrites;
    // The codec's name in error messages, and the largest code point it can write.
    private readonly string _codec;
    private readonly int _limit;
    private string _current = "";

    private TextFile(string name, string mode, string encoding, TextReader? reader, TextWriter? writer, Stream? stream,
        bool translate, bool translateWrites, string codec, int limit)
    {
        this.name = name;
        this.mode = mode;
        this.encoding = encoding;
        (_reader, _writer, _stream) = (reader, writer, stream);
        (_translate, _translateWrites, _codec, _limit) = (translate, translateWrites, codec, limit);
    }

    public string @name { get; }

    public string @mode { get; }

    public string @encoding { get; }

    public bool @closed { get; private set; }

    /// <summary>
    /// <c>open(file, mode, encoding=…, newline=…)</c>. The compiler has checked that the mode,
    /// encoding and newline are ones §6.1 allows.
    /// </summary>
    public static TextFile @open(string @file, string @mode, string @encoding, string? @newline)
    {
        var (codec, text, limit) = Codec(encoding);
        var kind = mode.Replace("t", "");
        var stream = OpenStream(file, kind);
        TextReader? reader = null;
        TextWriter? writer = null;
        if (kind == "r")
            reader = new StreamReader(stream, text, detectEncodingFromByteOrderMarks: false);
        else
            writer = new StreamWriter(stream, text) { AutoFlush = false };
        var opened = new TextFile(file, mode, encoding, reader, writer, stream, newline is null, newline is null && Environment.NewLine != "\n", codec, limit);
        Open.Add(opened);
        return opened;
    }

    internal static TextFile Stdin() =>
        new("<stdin>", "r", "utf-8", Io.In, null, null, translate: true, translateWrites: false, "utf-8", int.MaxValue);

    internal static TextFile Stdout(bool error) =>
        new(error ? "<stderr>" : "<stdout>", "w", "utf-8", null, error ? Io.Error : Io.Out, null, translate: true, translateWrites: false, "utf-8", int.MaxValue);

    /// <summary>The program has ended: flushes and closes every file it left open (SPEC §6.1).</summary>
    internal static void CloseAll()
    {
        foreach (var file in Open.ToList())
        {
            try
            {
                file.close();
            }
            catch (Exception)
            {
                // The program has already ended; a file that can't be flushed now is lost, as in Python.
            }
        }
        Open.Clear();
    }

    // Python's codec names (SPEC §6.1): the name in messages, the .NET encoding, and the largest code
    // point it holds. Each throws on bytes it can't decode.
    private static (string Codec, Encoding Text, int Limit) Codec(string encoding) =>
        encoding.ToLowerInvariant().Replace('_', '-') switch
        {
            "utf-8-sig" or "utf8-sig" => ("utf-8", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), int.MaxValue),
            "ascii" or "us-ascii" => ("ascii", Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), 0x7F),
            "latin-1" or "latin1" or "iso-8859-1" or "iso8859-1" or "l1" => ("latin-1", Encoding.Latin1, 0xFF),
            _ => ("utf-8", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), int.MaxValue),
        };

    // Opens the file, raising the OSError Python would, with Linux's errno values (SPEC §6.1).
    private static FileStream OpenStream(string file, string kind)
    {
        if (Directory.Exists(file))
            throw kind == "x" ? new FileExistsError(17, "File exists", file) : new IsADirectoryError(21, "Is a directory", file);
        if (kind == "x" && File.Exists(file))
            throw new FileExistsError(17, "File exists", file);
        var (fileMode, access) = kind switch
        {
            "r" => (FileMode.Open, FileAccess.Read),
            "w" => (FileMode.Create, FileAccess.Write),
            "a" => (FileMode.Append, FileAccess.Write),
            _ => (FileMode.CreateNew, FileAccess.Write),
        };
        try
        {
            return new FileStream(file, fileMode, access, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileNotFoundError(2, "No such file or directory", file);
        }
        catch (UnauthorizedAccessException)
        {
            throw new PermissionError(13, "Permission denied", file);
        }
        catch (IOException e)
        {
            throw new OSError(e.Message);
        }
    }

    public string @read()
    {
        var reader = Readable();
        var text = new StringBuilder();
        while (ReadChar(reader) is var c and >= 0)
            text.Append((char)c);
        return text.ToString();
    }

    /// <summary><c>read(n)</c>: at most <paramref name="n"/> code points.</summary>
    public string @read(long @n)
    {
        if (n < 0)
            return read();
        var reader = Readable();
        var text = new StringBuilder();
        for (long count = 0; count < n && ReadChar(reader) is var c and >= 0; count++)
        {
            text.Append((char)c);
            if (char.IsHighSurrogate((char)c) && ReadChar(reader) is var low and >= 0)
                text.Append((char)low);
        }
        return text.ToString();
    }

    public string @readline() => ReadLine(Readable());

    public List<string> @readlines()
    {
        var reader = Readable();
        var lines = new List<string>();
        while (ReadLine(reader) is { Length: > 0 } line)
            lines.Add(line);
        return lines;
    }

    public long @write(string @s)
    {
        var writer = Writable();
        foreach (var rune in s.EnumerateRunes())
        {
            if (rune.Value > _limit)
                throw new UnicodeEncodeError($"'{_codec}' codec can't encode character '{Escape(rune)}'");
        }
        writer.Write(_translateWrites ? s.Replace("\n", Environment.NewLine) : s);
        return Builtins.Len(s);
    }

    public void @writelines(IEnumerable<string> @lines)
    {
        foreach (var line in lines)
            write(line);
    }

    public void @flush()
    {
        if (closed)
            throw Closed();
        _writer?.Flush();
    }

    public void @close()
    {
        if (closed)
            return;
        closed = true;
        Open.Remove(this);
        if (_stream is null)
        {
            // A standard stream belongs to the host; closing it only stops the program using it.
            _writer?.Flush();
            return;
        }
        try
        {
            _writer?.Flush();
        }
        finally
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _stream.Dispose();
        }
    }

    public TextFile @__enter__()
    {
        if (closed)
            throw Closed();
        return this;
    }

    /// <summary>A file's <c>__exit__</c> closes it, and never suppresses the exception (SPEC §3.17).</summary>
    public void @__exit__(ClassValue? @exc_type, TaipanBaseException? @exc, Traceback? @tb) => close();

    /// <summary><c>print</c>'s writer for <c>file=</c> (SPEC §6).</summary>
    internal void Print(string text, bool flush)
    {
        write(text);
        if (flush)
            this.flush();
    }

    public bool MoveNext()
    {
        _current = readline();
        return _current.Length > 0;
    }

    public string Current => _current;

    public IEnumerator<string> GetEnumerator()
    {
        while (MoveNext())
            yield return Current;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() =>
        $"<_io.TextIOWrapper name={Display.PythonRepr(name)} mode={Display.PythonRepr(mode)} encoding={Display.PythonRepr(encoding)}>";

    private TextReader Readable()
    {
        if (closed)
            throw Closed();
        return _reader ?? throw new OSError("not readable");
    }

    private TextWriter Writable()
    {
        if (closed)
            throw Closed();
        return _writer ?? throw new OSError("not writable");
    }

    private static ValueError Closed() => new("I/O operation on closed file.");

    // One line with its ending, or "" at the end. Without translation, a line also ends at a lone
    // "\r", and "\r\n" stays as it is (SPEC §6.1).
    private string ReadLine(TextReader reader)
    {
        var line = new StringBuilder();
        while (ReadChar(reader) is var c and >= 0)
        {
            line.Append((char)c);
            if (c == '\n')
                break;
            if (c == '\r')
            {
                if (Peek(reader) == '\n')
                    line.Append((char)Read(reader));
                break;
            }
        }
        return line.ToString();
    }

    // The next character, with "\r\n" and "\r" read as "\n" under universal newlines.
    private int ReadChar(TextReader reader)
    {
        var c = Read(reader);
        if (c != '\r' || !_translate)
            return c;
        if (Peek(reader) == '\n')
            Read(reader);
        return '\n';
    }

    private int Read(TextReader reader)
    {
        try
        {
            return reader.Read();
        }
        catch (DecoderFallbackException e)
        {
            throw DecodeError(e);
        }
    }

    private int Peek(TextReader reader)
    {
        try
        {
            return reader.Peek();
        }
        catch (DecoderFallbackException e)
        {
            throw DecodeError(e);
        }
    }

    private UnicodeDecodeError DecodeError(DecoderFallbackException e) =>
        new($"'{_codec}' codec can't decode byte 0x{(e.BytesUnknown is [var first, ..] ? first : 0):x2}");

    // A character as Python's encode errors write it: \xe9, €, \U0001f40d.
    private static string Escape(Rune rune) => rune.Value switch
    {
        < 0x100 => $"\\x{rune.Value:x2}",
        < 0x10000 => $"\\u{rune.Value:x4}",
        _ => $"\\U{rune.Value:x8}",
    };
}
