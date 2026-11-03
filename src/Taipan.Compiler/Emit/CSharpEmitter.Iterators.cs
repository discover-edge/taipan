using Taipan.Compiler.Semantics;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Emit;

// Iterators, generators and async code (SPEC §3.13, §3.14, §3.20, §8; M31, M33, M42).
public sealed partial class CSharpEmitter
{
    private enum CoroutineKind { Async, Generator, AsyncGenerator }

    // The coroutine whose body is being emitted, and its generator's channel; null in plain code.
    private (CoroutineKind Kind, string? Channel)? _coroutine;

    /// <summary>
    /// A function's body as its frame (SPEC §3.19). An async function's is a local async lambda that
    /// <c>Async.Start</c> runs, and a generator's a local async function that its generator object
    /// drives as a coroutine (SPEC §8), so none of a generator's body runs until a value is asked for.
    /// </summary>
    private void EmitFrame(FunctionSymbol function, TextSpan span, string name, Action body)
    {
        var outer = _coroutine;
        if (!function.IsGenerator && !function.IsAsync)
        {
            _coroutine = null;
            EmitTracked(span, name, body);
            _coroutine = outer;
            return;
        }
        if (!function.IsGenerator)
        {
            _coroutine = (CoroutineKind.Async, null);
            var result = ((AwaitableOfType)Concrete(function.ReturnType)).Result;
            Line($"return {Runtime}.Async.Start{(result is NothingType ? "" : $"<{TypeName(result)}>")}(async () =>");
            Line("{");
            _indent++;
            EmitTracked(span, name, body);
            _indent--;
            Line("});");
            _coroutine = outer;
            return;
        }
        var declared = Concrete(function.ReturnType);
        var (yielded, sent, returned) = declared switch
        {
            GeneratorOfType generator => (generator.Yielded, generator.Sent, generator.Returned),
            AsyncGeneratorOfType generator => (generator.Yielded, generator.Sent, TaipanType.None),
            _ => (TypeChecker.ElementOf(declared) ?? TypeChecker.AsyncElementOf(declared) ?? TaipanType.Error, TaipanType.None, TaipanType.None),
        };
        var isAsync = function.IsAsync;
        var channel = $"@{_locals.Fresh("channel")}";
        var bodyName = $"@{_locals.Fresh("body")}";
        var types = $"{TypeName(yielded)}, {TypeName(sent)}";
        _coroutine = (isAsync ? CoroutineKind.AsyncGenerator : CoroutineKind.Generator, channel);
        Line(isAsync
            ? $"async {Tasks} {bodyName}({Runtime}.AsyncGeneratorChannel<{types}> {channel})"
            : $"async {Tasks}<{TypeName(returned)}> {bodyName}({Runtime}.GeneratorChannel<{types}> {channel})");
        Line("{");
        _indent++;
        EmitTracked(span, name, body);
        // Reaching the end returns None (SPEC §3.20).
        if (!isAsync)
            Line("return default!;");
        _indent--;
        Line("}");
        _coroutine = outer;
        // next() can't resume a yield whose value is used unless what it receives accepts None.
        var needs = TypeChecker.IsAssignable(TaipanType.None, sent) ? "" : $", \"{Escape(sent.ToString())}\"";
        Line(isAsync
            ? $"return new {Runtime}.AsyncGenerator<{types}>({bodyName}, \"{function.Name}\"{needs});"
            : $"return new {Runtime}.Generator<{types}, {TypeName(returned)}>({bodyName}, \"{function.Name}\"{needs});");
    }

    /// <summary><c>await e</c> (SPEC §8): through the runtime, which resumes on the event loop and can cancel.</summary>
    private string Await(BoundExpr operand) => $"await {Runtime}.Async.Await({Expr(operand)})";

    /// <summary><c>yield</c> and <c>yield from</c> (SPEC §8): an await of the generator's channel.</summary>
    private string Yield(BoundYield yield)
    {
        var channel = _coroutine!.Value.Channel;
        if (yield.From)
            return $"await {channel}.From({Expr(yield.Value!)})";
        return $"await {channel}.{(yield.Used ? "Receive" : "Yield")}({(yield.Value is null ? "default!" : Expr(yield.Value))})";
    }

    /// <summary><c>asyncio.gather(…)</c>: each awaitable with how its result reads as the joined type (SPEC §3.20).</summary>
    private string Gather(BoundGather gather)
    {
        var element = TypeName(gather.Element);
        var awaited = $"{Runtime}.Awaited<{element}>";
        var items = gather.Items.Select(item =>
        {
            if (item.Spread)
                return $".. {awaited}.Each({Expr(item.Value)})";
            var result = ((AwaitableOfType)Concrete(item.Value.Type)).Result;
            if (result is NothingType || Concrete(result) == Concrete(gather.Element))
                return $"new {awaited}({Expr(item.Value)})";
            var value = $"@{_locals.Fresh("value")}";
            return $"{awaited}.From({Expr(item.Value)}, static ({TypeName(result)} {value}) => ({element}){value})";
        });
        return $"{Runtime}.Asyncio.gather<{element}>([{string.Join(", ", items)}])";
    }

    /// <summary><c>async for</c> (SPEC §8): a loop that awaits <c>__anext__()</c> until <c>StopAsyncIteration</c>.</summary>
    private void EmitAsyncFor(BoundForEach forEach)
    {
        var iterator = $"@{_locals.Fresh("iterator")}";
        var variable = $"@{_locals[forEach.Variable]}";
        LineDirective(forEach.Span);
        Line($"var {iterator} = {Expr(forEach.Iterable)}.@__aiter__();");
        Line("while (true)");
        Line("{");
        _indent++;
        Line($"{TypeName(forEach.Variable.Type)} {variable};");
        Line("try");
        Line("{");
        Line($"    {variable} = await {Runtime}.Async.Await({iterator}.@__anext__());");
        Line("}");
        Line($"catch ({Runtime}.StopAsyncIteration)");
        Line("{");
        Line("    break;");
        Line("}");
        EmitLoopBody(forEach.Body, line: LineOf(forEach.Span));
        _indent--;
        Line("}");
    }

    /// <summary>
    /// A value as an iterable or iterator (SPEC §8): a list, dict or str through the runtime's
    /// guarded loop, a class through its <c>__iter__</c> or <c>__next__</c>. A generator is one
    /// already, and since M42 a class is an async one through its <c>__aiter__</c> or <c>__anext__</c>.
    /// </summary>
    private string ToIterable(BoundToIterable converted)
    {
        var value = Expr(converted.Value);
        var type = Concrete(converted.Value.Type);
        var target = Concrete(converted.Type);
        if (type is GeneratorOfType or AsyncGeneratorOfType or AsyncIteratorOfType)
            return value;
        if (type is ClassType asyncClass && target is AsyncIteratorOfType or AsyncIterableOfType)
        {
            var element = TypeName(target is AsyncIteratorOfType iterator ? iterator.Element : ((AsyncIterableOfType)target).Element);
            var self = $"@{_locals.Fresh("self")}";
            return target is AsyncIteratorOfType || asyncClass.Symbol.FindMethod("__aiter__") is null
                ? $"{Runtime}.AsyncIterators.FromAnext({value}, static ({TypeName(type)} {self}) => {self}.@__anext__())"
                : $"{Runtime}.AsyncIterators.FromAiter<{TypeName(type)}, {element}>({value}, static {self} => {self}.@__aiter__())";
        }
        if (type is ClassType classType)
        {
            var element = TypeName(target is IteratorOfType iterator ? iterator.Element : ((IterableOfType)target).Element);
            var self = $"@{_locals.Fresh("self")}";
            return target is IteratorOfType || classType.Symbol.FindMethod("__iter__") is null
                ? $"{Runtime}.Iterators.FromNext({value}, static ({TypeName(type)} {self}) => {self}.@__next__())"
                : $"{Runtime}.Iterators.FromIter<{TypeName(type)}, {element}>({value}, static {self} => {self}.@__iter__())";
        }
        return $"{Runtime}.Iter.Over({value})";
    }

    /// <summary>What <c>iter(x)</c>'s iterator displays as: Python's names for a list's, or a general one.</summary>
    private string IteratorName(BoundExpr argument) =>
        (argument is BoundToIterable converted ? Concrete(converted.Value.Type) : Concrete(argument.Type)) switch
        {
            ListOfType => "list_iterator object",
            _ => "iterator object",
        };
}
