using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sallyport.Profile;
using Sallyport.Protocol;
using Sallyport.Runtime;

namespace Sallyport.Tests;

/// <summary>An in-process server for each connection attempt, picked by the attempt's number: a <see cref="FakeServer"/>,
/// or null for a server that never answers.</summary>
internal sealed class ServerFarm(TestPki pki, byte[] key) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(60));
    private readonly List<Task> _serving = [];
    private int _attempts;

    public Func<int, FakeServer?> ServerFor { get; init; } = _ => new FakeServer();
    public int Attempts => Volatile.Read(ref _attempts);

    public TunnelSessionOptions Options(OvpnProfile profile, TimeSpan? connectTimeout = null) => new()
    {
        FirstBackoff = TimeSpan.FromMilliseconds(50),
        LongestBackoff = TimeSpan.FromMilliseconds(200),
        Connect = (remote, ct) =>
        {
            var (client, server) = MemoryTransport.Pair();
            if (ServerFor(Interlocked.Increment(ref _attempts)) is { } fake)
                lock (_serving) _serving.Add(fake.RunAsync(server, key, pki.Server, _stop.Token));
            return Tunnel.ConnectAsync(profile, remote, _ => Task.FromResult<ITransport>(client), connectTimeout ?? TimeSpan.FromSeconds(30),
                new TunnelOptions(), null, ct);
        },
    };

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        Task[] all;
        lock (_serving) all = [.. _serving];
        // A server ends however its client left it — EXIT, RESTART or a closed transport; none of that is under test here.
        try { await Task.WhenAll(all); } catch (Exception) { }
        _stop.Dispose();
    }
}

/// <summary>Every status a session reported, and a way to wait for one.</summary>
internal sealed class StatusLog
{
    private readonly object _gate = new();
    private readonly List<SessionStatus> _all = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Add(SessionStatus status)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            _all.Add(status);
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    public List<SessionStatus> All { get { lock (_gate) return [.. _all]; } }

    /// <summary>The <paramref name="count"/>th status that matches, once it has been reported.</summary>
    public async Task<SessionStatus> WaitAsync(Func<SessionStatus, bool> match, int count = 1)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                var hits = _all.Where(match).ToList();
                if (hits.Count >= count) return hits[count - 1];
                changed = _changed.Task;
            }
            await changed.WaitAsync(limit.Token);
        }
    }
}

public class TunnelSessionTests
{
    [Fact]
    public async Task ASessionReportsConnectingThenUpWithItsAddressAndStopsWhenCancelled()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var profile = OvpnProfile.Parse(pki.Profile(key));
        await using var farm = new ServerFarm(pki, key);
        await using var session = new TunnelSession(profile, farm.Options(profile));
        var log = new StatusLog();
        session.StatusChanged += log.Add;
        using var stop = new CancellationTokenSource();
        var running = session.RunAsync(stop.Token);

        var up = await log.WaitAsync(s => s.State == SessionState.Up);
        Assert.Equal("udp/1194", up.Remote);
        Assert.Equal("10.8.0.2", up.Address);
        Assert.NotNull(session.Network);
        await stop.CancelAsync();
        await running;

        Assert.Equal([SessionState.Connecting, SessionState.Up, SessionState.Stopped], log.All.Select(s => s.State));
        Assert.Null(session.Network);
        Assert.Null(session.Status.Address);
    }

    [Fact]
    public async Task WhenTheServerSaysRestartTheSessionConnectsAgainAndKeepsItsByteCounts()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var profile = OvpnProfile.Parse(pki.Profile(key));
        var restart = new TaskCompletionSource();
        await using var farm = new ServerFarm(pki, key) { ServerFor = n => n == 1 ? new FakeServer { RestartWhen = restart } : new FakeServer() };
        await using var session = new TunnelSession(profile, farm.Options(profile));
        var log = new StatusLog();
        session.StatusChanged += log.Add;
        using var stop = new CancellationTokenSource();
        var running = session.RunAsync(stop.Token);
        await log.WaitAsync(s => s.State == SessionState.Up);

        // Twenty bytes the server echoes: counted out, and in again, by the first tunnel.
        await session.CurrentTunnel!.SendAsync(new byte[] { 0x45, 0, 0, 20, 0, 0, 0, 0, 64, 17, 0, 0, 10, 8, 0, 2, 192, 0, 2, 1 });
        for (var i = 0; i < 200 && session.Status.BytesIn < 20; i++) await Task.Delay(10);
        restart.SetResult();
        var again = await log.WaitAsync(s => s.State == SessionState.Up, count: 2);
        var status = session.Status;
        await stop.CancelAsync();
        await running;

        Assert.Equal(1, again.Reconnects);
        Assert.Contains("RESTART", log.All.First(s => s.State == SessionState.Reconnecting).LastError);
        Assert.Equal(2, farm.Attempts);
        Assert.Equal(20, status.BytesOut);
        Assert.Equal(20, status.BytesIn);
    }

    [Fact]
    public async Task ARemoteThatNeverAnswersIsPassedOverForTheNext()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var profile = OvpnProfile.Parse(pki.Profile(key) + "\nremote vpn2.example.test 443 tcp\n");
        Assert.Equal(2, profile.Remotes.Count);
        await using var farm = new ServerFarm(pki, key) { ServerFor = n => n == 1 ? null : new FakeServer() };
        await using var session = new TunnelSession(profile, farm.Options(profile, connectTimeout: TimeSpan.FromSeconds(1)));
        var log = new StatusLog();
        session.StatusChanged += log.Add;
        using var stop = new CancellationTokenSource();
        var running = session.RunAsync(stop.Token);

        var up = await log.WaitAsync(s => s.State == SessionState.Up);
        await stop.CancelAsync();
        await running;

        Assert.Equal("tcp/443", up.Remote);
        Assert.Equal(2, up.Attempts);
        var second = log.All[1];
        // Still "connecting": no tunnel has been up yet. The first remote's silence is what it says went wrong.
        Assert.Equal((SessionState.Connecting, "tcp/443"), (second.State, second.Remote));
        Assert.Contains("no answer", second.LastError);
    }

    [Fact]
    public async Task WhenNoRemoteAnswersItWaitsAndTriesAgainUntilStopped()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var profile = OvpnProfile.Parse(pki.Profile(key));
        await using var farm = new ServerFarm(pki, key) { ServerFor = _ => null };
        await using var session = new TunnelSession(profile, farm.Options(profile, connectTimeout: TimeSpan.FromMilliseconds(300)));
        var log = new StatusLog();
        session.StatusChanged += log.Add;
        using var stop = new CancellationTokenSource();
        var running = session.RunAsync(stop.Token);

        // A round with no answer ends in a report with no remote: waiting before the next.
        await log.WaitAsync(s => s.State == SessionState.Reconnecting && s.Remote is null, count: 2);
        await stop.CancelAsync();
        await running;

        Assert.True(farm.Attempts >= 2);
        Assert.Equal(SessionState.Stopped, session.Status.State);
        Assert.DoesNotContain(log.All, s => s.State == SessionState.Up);
    }

    [Fact]
    public async Task AProfileItCannotServeIsRefusedWithoutAnAttempt()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var profile = OvpnProfile.Parse(pki.Profile(key) + "\nauth-user-pass\n");
        await using var farm = new ServerFarm(pki, key);
        await using var session = new TunnelSession(profile, farm.Options(profile));

        await session.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionState.Refused, session.Status.State);
        Assert.Contains("username and password", session.Status.LastError);
        Assert.Equal(0, farm.Attempts);
    }
}

public class TunnelServiceTests
{
    [Fact]
    public async Task TheProxyBindsOnceTheTunnelIsUpAndTheStatusKeepsTheShapeKoboReads()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var profile = OvpnProfile.Parse(pki.Profile(key));
        await using var farm = new ServerFarm(pki, key);
        var statuses = new List<TunnelServiceStatus>();
        await using var service = new TunnelService(profile, new IPEndPoint(IPAddress.Loopback, 0), s => { lock (statuses) statuses.Add(s); },
            farm.Options(profile));
        using var stop = new CancellationTokenSource();
        var running = service.RunAsync(stop.Token);
        for (var i = 0; i < 500 && service.Status.State != TunnelServiceStatus.Up; i++) await Task.Delay(20);

        var up = service.Status;
        using (var client = new TcpClient()) await client.ConnectAsync(IPAddress.Loopback, up.ProxyPort!.Value);
        await stop.CancelAsync();
        await running;

        TunnelServiceStatus[] seen;
        lock (statuses) seen = [.. statuses];
        Assert.Equal(TunnelServiceStatus.Connecting, seen[0].State);
        Assert.Null(seen[0].ProxyPort);
        Assert.Equal("10.8.0.2", up.Address);
        Assert.Equal(TunnelServiceStatus.Stopped, seen[^1].State);
        // Kobo reads these names from the Host's status file.
        var json = JsonSerializer.Serialize(up, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        foreach (var name in new[] { "state", "since", "remote", "attempts", "reconnects", "lastError", "address", "openConnections", "bytesUp", "bytesDown", "proxyPort" })
            Assert.Contains($"\"{name}\"", json);
    }
}

/// <summary>Seals by flipping bits under a one-byte key it checks on the way back: enough to prove a store never writes
/// the text itself, and that a profile sealed under one key does not open under another.</summary>
internal sealed class FakeProtector(byte keyId) : IProfileProtector
{
    public int Sealed;

    public byte[] Protect(byte[] plain)
    {
        Sealed++;
        return [keyId, .. plain.Select(b => (byte)(b ^ 0x5A))];
    }

    public byte[] Unprotect(byte[] sealedBytes) =>
        sealedBytes.Length > 0 && sealedBytes[0] == keyId
            ? [.. sealedBytes.Skip(1).Select(b => (byte)(b ^ 0x5A))]
            : throw new CryptographicException("sealed under another key");
}

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sallyport-tests", Guid.NewGuid().ToString("N"));
    private readonly TestPki _pki = new();
    private readonly byte[] _key = TestKeys.StaticKey();

    public void Dispose()
    {
        _pki.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ImportKeepsASealedCopyAndDescribesItWithoutKeysOrHostNames()
    {
        var protector = new FakeProtector(1);
        var store = new ProfileStore(_directory, protector);
        var text = _pki.Profile(_key);

        var entry = store.Import("  Tokyo  ", text);

        Assert.Equal("Tokyo", entry.Name);
        Assert.Equal(["udp/1194"], entry.Remotes);
        Assert.Null(entry.Unsupported);
        Assert.Matches("^[0-9a-f]{16}$", entry.Id);
        Assert.Equal(entry.Id, Assert.Single(store.List()).Id);
        Assert.Equal(1, protector.Sealed);
        var sealedText = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_directory, entry.Id + ".profile")));
        var description = File.ReadAllText(Path.Combine(_directory, entry.Id + ".json"));
        Assert.DoesNotContain("BEGIN", sealedText);
        Assert.DoesNotContain("BEGIN", description);
        Assert.DoesNotContain("vpn.example.test", description);

        var original = OvpnProfile.Parse(text);
        var loaded = store.Load(entry.Id);
        Assert.Equal(original.KeyPem, loaded.KeyPem);
        Assert.Equal(original.CertPem, loaded.CertPem);
        Assert.Equal(original.TlsCryptKey, loaded.TlsCryptKey);
        Assert.Equal(original.Remotes, loaded.Remotes);
    }

    [Fact]
    public void AProfileThisVersionCannotServeIsKeptWithTheReason()
    {
        var store = new ProfileStore(_directory, new FakeProtector(1));

        var entry = store.Import("work", _pki.Profile(_key) + "\nauth-user-pass\n");

        Assert.Contains("username and password", entry.Unsupported);
    }

    [Fact]
    public void TextThatIsNotAProfileIsRefusedWithoutBeingQuoted()
    {
        var store = new ProfileStore(_directory, new FakeProtector(1));

        var refused = Assert.Throws<InvalidDataException>(() => store.Import("x", "SECRET-MARKER is not a profile"));

        Assert.DoesNotContain("SECRET-MARKER", refused.Message);
        Assert.Empty(store.List());
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ANameIsRequired(string name)
    {
        var store = new ProfileStore(_directory, new FakeProtector(1));

        Assert.Throws<ArgumentException>(() => store.Import(name, _pki.Profile(_key)));
    }

    [Fact]
    public void RemoveDeletesBothHalves()
    {
        var store = new ProfileStore(_directory, new FakeProtector(1));
        var entry = store.Import("Tokyo", _pki.Profile(_key));

        Assert.True(store.Remove(entry.Id));

        Assert.Empty(store.List());
        Assert.Empty(Directory.EnumerateFiles(_directory));
        Assert.False(store.Remove(entry.Id));
    }

    [Theory]
    [InlineData("../../secret")]
    [InlineData(@"..\..\secret")]
    [InlineData("0123456789ABCDEF")]
    [InlineData("0123456789abcde")]
    public void AnIdFromOutsideCannotNameAPath(string id)
    {
        var store = new ProfileStore(_directory, new FakeProtector(1));

        Assert.Throws<ArgumentException>(() => store.Load(id));
        Assert.Throws<ArgumentException>(() => store.Remove(id));
    }

    [Fact]
    public void AnIdTheStoreDoesNotHoldIsNotFound()
    {
        var store = new ProfileStore(_directory, new FakeProtector(1));

        Assert.Throws<FileNotFoundException>(() => store.Load("0123456789abcdef"));
    }

    [Fact]
    public void AProfileSealedUnderAnotherKeyDoesNotOpen()
    {
        var entry = new ProfileStore(_directory, new FakeProtector(1)).Import("Tokyo", _pki.Profile(_key));

        Assert.Throws<CryptographicException>(() => new ProfileStore(_directory, new FakeProtector(2)).Load(entry.Id));
    }

    [Fact]
    public void ADescriptionWithoutItsSealedHalfIsNotOffered()
    {
        var store = new ProfileStore(_directory, new FakeProtector(1));
        var entry = store.Import("Tokyo", _pki.Profile(_key));

        File.Delete(Path.Combine(_directory, entry.Id + ".profile"));

        Assert.Empty(store.List());
    }
}
