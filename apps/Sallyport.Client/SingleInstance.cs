using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Sallyport.Client;

/// <summary>
/// One client per signed-in user: a second would fight the first for the same SOCKS ports. A second start hands its
/// arguments — a profile to import, or nothing, meaning "show the window" — to the first and leaves.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    // Pipe names are machine-wide; the session keeps two signed-in users apart, CurrentUserOnly keeps anyone else out.
    private static readonly string PipeName = $"Sallyport.Client.{Process.GetCurrentProcess().SessionId}";
    private const int LongestMessage = 64 * 1024;

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>This process's claim to be the client, or null when another already is — after handing it
    /// <paramref name="args"/>.</summary>
    public static SingleInstance? Claim(string[] args)
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\Sallyport.Client", out var first);
        if (first) return new SingleInstance(mutex);
        mutex.Dispose();
        HandOff(args);
        return null;
    }

    public void Listen(Action<string[]> received) => _ = Task.Run(() => ServeAsync(received));

    private async Task ServeAsync(Action<string[]> received)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                var buffer = new byte[LongestMessage];
                var length = 0;
                int read;
                while (length < buffer.Length && (read = await pipe.ReadAsync(buffer.AsMemory(length), _stop.Token)) > 0) length += read;
                received(Encoding.UTF8.GetString(buffer, 0, length).Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { /* a caller that left half-way: wait for the next */ }
        }
    }

    private static void HandOff(string[] args)
    {
        try
        {
            // Not CurrentUserOnly here: it compares the pipe's owner with this token's, which for an elevated start (setup
            // closing the client, say) is Administrators, not the person. The person is checked instead: the server must
            // run as this same user. The server's own end stays CurrentUserOnly.
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None);
            pipe.Connect(3000);
            if (Sallyport.Windows.Service.PipePeer.ServerOf(pipe).User is not { } owner || owner != System.Security.Principal.WindowsIdentity.GetCurrent().User) return;
            // Paths made whole here: the running client has its own working directory.
            var message = string.Join('\n', args.Select(a => a.StartsWith('-') ? a : Path.GetFullPath(a)));
            pipe.Write(Encoding.UTF8.GetBytes(message));
        }
        // The first is still starting, or already leaving: there is no one to hand to, and nothing worse than a
        // window not shown.
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _stop.Dispose();
    }
}
