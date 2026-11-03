using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Taipan.Tests;

/// <summary>
/// M33 acceptance (SPEC §13.11): <c>await HttpClient().GetStringAsync(url)</c> against a local test
/// server prints the response.
/// </summary>
public class AsyncTests
{
    [Fact]
    public async Task AwaitsAnHttpRequest()
    {
        var port = FreePort();
        var url = $"http://127.0.0.1:{port}/greeting/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();
        var served = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var body = Encoding.UTF8.GetBytes($"hello from {context.Request.Url!.AbsolutePath}");
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });

        var start = new ProcessStartInfo("dotnet", [Path.Combine(AppContext.BaseDirectory, "taipan.dll"), "run", "tests/cases/m33/http/main.tpn"])
        {
            WorkingDirectory = RepoPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["TAIPAN_TEST_URL"] = url;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        await served.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("", await error);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("hello from /greeting/\n", await output);
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
