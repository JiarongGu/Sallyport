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

    public TunnelSessionOptions Options(OvpnProfile profile, TimeSpan? connectTimeout = null, ConnectionPolicy? policy = null,
        Func<Tunnel, int, IAsyncDisposable>? consumer = null, Random? random = null) => new()
    {
        Policy = policy ?? new ConnectionPolicy
        {
            RetryWait = TimeSpan.FromMilliseconds(50),
            RetryWaitMax = TimeSpan.FromMilliseconds(200),
            AttemptTimeout = connectTimeout ?? TimeSpan.FromSeconds(30),
        },
        Consumer = consumer,
        Random = random ?? Random.Shared,
        Connect = (remote, timeout, ct) =>
        {
            Connected.Enqueue(remote);
            var (client, server) = MemoryTransport.Pair();
            if (ServerFor(Interlocked.Increment(ref _attempts)) is { } fake)
                lock (_serving) _serving.Add(fake.RunAsync(server, key, pki.Server, _stop.Token));
            return Tunnel.ConnectAsync(profile, remote, _ => Task.FromResult<ITransport>(client), timeout, new TunnelOptions(), null, ct);
        },
    };

    /// <summary>Every remote an attempt went to, in order.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<Remote> Connected { get; } = new();

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
        await using var session = new TunnelSession(profile, farm.Options(profile, connectTimeout: TimeSpan.FromSeconds(3)));
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

        // A round with no answer ends in a report with no remote: waiting before the next, until a time it says.
        var waiting = await log.WaitAsync(s => s.Remote is null && s.NextAttemptAt is not null, count: 2);
        await stop.CancelAsync();
        await running;

        Assert.True(farm.Attempts >= 2);
        Assert.NotNull(waiting.NextAttemptAt);
        Assert.InRange(waiting.NextAttemptAt!.Value - waiting.Since, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(250));
        Assert.True(waiting.FailedRounds >= 1);
        // Never up: still connecting, not reconnecting.
        Assert.Equal(SessionState.Connecting, waiting.State);
        // Trying, it says no time: a countdown is shown only while one is kept.
        Assert.All(log.All.Where(s => s.Remote is not null), s => Assert.Null(s.NextAttemptAt));
        Assert.Equal(SessionState.Stopped, session.Status.State);
        Assert.DoesNotContain(log.All, s => s.State == SessionState.Up);
    }

    [Fact]
    public async Task ARoundLimitEndsTheSessionAsGivenUp()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var profile = OvpnProfile.Parse(pki.Profile(key) + "\nremote vpn2.example.test 443 tcp\n");
        await using var farm = new ServerFarm(pki, key) { ServerFor = _ => null };
        var policy = new ConnectionPolicy
        {
            RetryWait = TimeSpan.FromMilliseconds(20), RetryWaitMax = TimeSpan.FromMilliseconds(20),
            AttemptTimeout = TimeSpan.FromMilliseconds(200), RoundsMax = 3,
        };
        await using var session = new TunnelSession(profile, farm.Options(profile, policy: policy));
        var log = new StatusLog();
        session.StatusChanged += log.Add;

        await session.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(SessionState.GaveUp, session.Status.State);
        // Three rounds of both remotes, and no fourth.
        Assert.Equal(6, farm.Attempts);
    }

    [Fact]
    public async Task AnAuthenticationRefusalEndsTheSessionUnlessThePolicyRetriesIt()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var profile = OvpnProfile.Parse(pki.Profile(key) + "\nremote vpn2.example.test 443 tcp\n");

        await using (var farm = new ServerFarm(pki, key) { ServerFor = _ => new FakeServer { RefuseAuthentication = true } })
        {
            await using var session = new TunnelSession(profile, farm.Options(profile));
            await session.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(SessionState.Refused, session.Status.State);
            Assert.Contains("refused authentication", session.Status.LastError);
            Assert.True(session.Status.AuthenticationRefused);
            // The second remote is not tried: the credentials are the profile's, not the server's.
            Assert.Equal(1, farm.Attempts);
        }

        await using (var farm = new ServerFarm(pki, key) { ServerFor = n => n == 1 ? new FakeServer { RefuseAuthentication = true } : new FakeServer() })
        {
            var policy = new ConnectionPolicy { RetryWait = TimeSpan.FromMilliseconds(20), RetryAfterAuthFailure = true };
            await using var session = new TunnelSession(profile, farm.Options(profile, policy: policy));
            var log = new StatusLog();
            session.StatusChanged += log.Add;
            using var stop = new CancellationTokenSource();
            var running = session.RunAsync(stop.Token);
            await log.WaitAsync(s => s.State == SessionState.Up);
            await stop.CancelAsync();
            await running;
            Assert.Equal(2, farm.Attempts);
        }
    }

    [Fact]
    public async Task AProtocolChosenTriesOnlyTheRemotesOfIt()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var profile = OvpnProfile.Parse(pki.Profile(key) + "\nremote vpn2.example.test 443 tcp\n");
        await using var farm = new ServerFarm(pki, key) { ServerFor = _ => new FakeServer() };
        await using var session = new TunnelSession(profile, farm.Options(profile, policy: profile.Policy with { Protocol = "tcp" }));
        var log = new StatusLog();
        session.StatusChanged += log.Add;
        using var stop = new CancellationTokenSource();
        var running = session.RunAsync(stop.Token);

        var up = await log.WaitAsync(s => s.State == SessionState.Up);
        await stop.CancelAsync();
        await running;

        // The UDP remote listed first is passed over without an attempt.
        Assert.Equal(("tcp/443", 1), (up.Remote, up.Attempts));
        Assert.Equal(["tcp"], farm.Connected.Select(r => r.Proto));
    }

    [Fact]
    public void AProtocolTheProfileHasNoRemoteOfIsRefusedBeforeItRuns()
    {
        using var pki = new TestPki();
        var profile = OvpnProfile.Parse(pki.Profile(TestKeys.StaticKey()));

        Assert.Throws<ArgumentException>(() => new TunnelSession(profile, new TunnelSessionOptions { Policy = profile.Policy with { Protocol = "tcp" } }));
    }

    [Fact]
    public async Task RemoteRandomShufflesEachRound()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var profile = OvpnProfile.Parse(pki.Profile(key) + "\nremote b.example.test 1194 udp\nremote c.example.test 1194 udp\nremote-random\n");
        Assert.True(profile.Policy.RandomOrder);
        await using var farm = new ServerFarm(pki, key) { ServerFor = _ => null };
        var policy = profile.Policy with
        {
            RetryWait = TimeSpan.FromMilliseconds(10), RetryWaitMax = TimeSpan.FromMilliseconds(10),
            AttemptTimeout = TimeSpan.FromMilliseconds(100), RoundsMax = 4,
        };
        await using var session = new TunnelSession(profile, farm.Options(profile, policy: policy, random: new Random(7)));

        await session.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        var hosts = farm.Connected.Select(r => r.Host).ToList();
        var rounds = Enumerable.Range(0, 4).Select(i => string.Join(",", hosts.Skip(i * 3).Take(3))).ToList();
        Assert.All(rounds, r => Assert.Equal(3, r.Split(',').Distinct().Count()));
        Assert.True(rounds.Distinct().Count() > 1, "four rounds in one order: not shuffled");
    }

    [Fact]
    public async Task EachTunnelGetsItsOwnConsumerAndItsGenerationIsSaid()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var profile = OvpnProfile.Parse(pki.Profile(key));
        var restart = new TaskCompletionSource();
        await using var farm = new ServerFarm(pki, key) { ServerFor = n => n == 1 ? new FakeServer { RestartWhen = restart } : new FakeServer() };
        var consumers = new List<Consumer>();
        var events = new List<string>();
        await using var session = new TunnelSession(profile, farm.Options(profile, consumer: (tunnel, generation) =>
        {
            var consumer = new Consumer(generation);
            lock (consumers) consumers.Add(consumer);
            return consumer;
        }));
        session.TunnelUp += (_, generation) => { lock (events) events.Add($"up {generation}"); };
        session.TunnelDown += (generation, _) => { lock (events) events.Add($"down {generation}"); };
        var log = new StatusLog();
        session.StatusChanged += log.Add;
        using var stop = new CancellationTokenSource();
        var running = session.RunAsync(stop.Token);
        await log.WaitAsync(s => s.State == SessionState.Up);
        restart.SetResult();
        await log.WaitAsync(s => s.State == SessionState.Up, count: 2);
        await stop.CancelAsync();
        await running;

        Assert.Equal([1, 2], consumers.Select(c => c.Generation));
        Assert.All(consumers, c => Assert.True(c.Disposed));
        Assert.Equal(["up 1", "down 1", "up 2", "down 2"], events);
        // A consumer of its own instead of the proxy's network.
        Assert.Null(session.Network);
    }

    [Fact]
    public async Task AConsumerThatThrowsAsItGoesDoesNotEndTheSession()
    {
        using var pki = new TestPki();
        var key = TestKeys.StaticKey();
        var profile = OvpnProfile.Parse(pki.Profile(key));
        var restart = new TaskCompletionSource();
        await using var farm = new ServerFarm(pki, key) { ServerFor = n => n == 1 ? new FakeServer { RestartWhen = restart } : new FakeServer() };
        await using var session = new TunnelSession(profile, farm.Options(profile, consumer: (_, generation) => new Throwing(generation)));
        var failures = new List<Exception>();
        session.ConsumerFailed += e => { lock (failures) failures.Add(e); };
        var log = new StatusLog();
        session.StatusChanged += log.Add;
        using var stop = new CancellationTokenSource();
        var running = session.RunAsync(stop.Token);
        await log.WaitAsync(s => s.State == SessionState.Up);
        restart.SetResult();
        await log.WaitAsync(s => s.State == SessionState.Up, count: 2);
        await stop.CancelAsync();
        await running;

        Assert.Equal(2, failures.Count);
        Assert.Equal(SessionState.Stopped, session.Status.State);
    }

    private sealed class Throwing(int generation) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException($"generation {generation} would not let go"));
    }

    private sealed class Consumer(int generation) : IAsyncDisposable
    {
        public int Generation { get; } = generation;
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void TheProfilesReconnectionDirectivesBecomeItsPolicy()
    {
        var policy = OvpnProfile.Parse(string.Join("\n",
            "client", "remote vpn.example.test 1194 udp", "connect-retry 5 120", "connect-retry-max 8", "remote-random",
            "server-poll-timeout 12", "auth-retry nointeract", "keepalive 15 90", "persist-tun", "pull-filter ignore \"ping-restart\""));

        Assert.Equal(TimeSpan.FromSeconds(5), policy.Policy.RetryWait);
        Assert.Equal(TimeSpan.FromSeconds(120), policy.Policy.RetryWaitMax);
        Assert.Equal(8, policy.Policy.RoundsMax);
        Assert.True(policy.Policy.RandomOrder);
        Assert.Equal(TimeSpan.FromSeconds(12), policy.Policy.AttemptTimeout);
        Assert.True(policy.Policy.RetryAfterAuthFailure);
        Assert.Equal((15, 90), (policy.Policy.Ping, policy.Policy.PingRestart));
        Assert.True(policy.PersistTun);
        Assert.True(policy.Policy.KeepaliveOverridesServer);

        var plain = OvpnProfile.Parse("remote vpn.example.test 1194 udp\nauth-retry none\n").Policy;
        Assert.Equal(new ConnectionPolicy(), plain);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 4)]
    [InlineData(6, 8)]
    [InlineData(9, 30)]
    public void TheWaitHoldsForFourRoundsThenDoublesToItsCeiling(int failedRounds, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), new ConnectionPolicy().WaitAfter(failedRounds));
    }

    [Fact]
    public void JitterStaysWithinItsFraction()
    {
        var policy = new ConnectionPolicy();
        var random = new Random(1);
        var waits = Enumerable.Range(0, 200).Select(_ => policy.WaitAfter(1, random).TotalSeconds).ToList();
        Assert.All(waits, w => Assert.InRange(w, 1.8, 2.2));
        Assert.True(waits.Distinct().Count() > 100);
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
        // Refused before asking, not by the server: no wrong password to say.
        Assert.False(session.Status.AuthenticationRefused);
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
    private static string WithoutBlock(string profile, string tag) =>
        System.Text.RegularExpressions.Regex.Replace(profile, $"<{tag}>.*?</{tag}>", "", System.Text.RegularExpressions.RegexOptions.Singleline);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sallyport-tests", Guid.NewGuid().ToString("N"));
    private readonly TestPki _pki = new();
    private readonly byte[] _key = TestKeys.StaticKey();

    public void Dispose()
    {
        _pki.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        // The shared parent too, once the last test is done with it: the tests leave nothing behind.
        try { Directory.Delete(Path.GetDirectoryName(_directory)!); }
        catch (IOException) { }
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
    public void APreviewSaysItsServersOnceEachAndKeepsNothing()
    {
        var text = _pki.Profile(_key) + "\nremote second.example.test 443 tcp\nremote VPN.example.test 1195 udp\n";

        var preview = ProfileStore.Preview(text);

        Assert.Equal(["vpn.example.test", "second.example.test"], preview.Servers);
        Assert.Equal(["udp", "tcp"], preview.Transports);
        Assert.Null(preview.Unsupported);
        Assert.False(Directory.Exists(_directory) && Directory.EnumerateFileSystemEntries(_directory).Any());
        // Asking for a username and password is a way to sign in (D29), not a reason it cannot connect.
        Assert.Null(ProfileStore.Preview(text + "auth-user-pass\n").Unsupported);
        Assert.Equal("the profile has no <ca>", ProfileStore.Preview(WithoutBlock(text, "ca")).Unsupported);
        Assert.Throws<InvalidDataException>(() => ProfileStore.Preview("SECRET-MARKER is not a profile"));
    }

    [Fact]
    public void ALoginIsKeptSealedWithItsPasswordOnlyWhenRememberedAndGoesWithTheProfile()
    {
        var store = new ProfileStore(_directory, new FakeProtector(1));
        var entry = store.Import("work", _pki.Profile(_key) + "\nauth-user-pass\n");

        Assert.True(entry.NeedsLogin);
        Assert.Equal(new LoginState(null, false), store.LoginOf(entry.Id));
        store.SetLogin(entry.Id, " alex ", null);
        Assert.Equal(new LoginState("alex", false), store.LoginOf(entry.Id));
        Assert.Null(store.SavedLogin(entry.Id));
        store.SetLogin(entry.Id, "alex", "correct horse");
        Assert.Equal(new LoginState("alex", true), store.LoginOf(entry.Id));
        Assert.Equal(("alex", "correct horse"), (store.SavedLogin(entry.Id)!.Username, store.SavedLogin(entry.Id)!.Password));
        var sealedLogin = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_directory, entry.Id + ".login")));
        Assert.DoesNotContain("alex", sealedLogin);
        Assert.DoesNotContain("horse", sealedLogin);
        Assert.DoesNotContain("alex", File.ReadAllText(Path.Combine(_directory, entry.Id + ".json")));

        Assert.Equal("work, renamed", store.Rename(entry.Id, " work, renamed ").Name);
        Assert.Equal(("work, renamed", true), (Assert.Single(store.List()).Name, store.LoginOf(entry.Id).PasswordSaved));
        store.Remove(entry.Id);
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public void APreviewSaysASignInIsNeededAndTheUsernameAnAccessServerProfileCarries()
    {
        var preview = ProfileStore.Preview("# OVPN_ACCESS_SERVER_USERNAME=alex\n" + _pki.Profile(_key) + "\nauth-user-pass\n");
        var withoutSignIn = ProfileStore.Preview("# OVPN_ACCESS_SERVER_USERNAME=alex\n" + _pki.Profile(_key));

        Assert.Equal((true, "alex"), (preview.NeedsLogin, preview.Username));
        Assert.Equal((false, null), (withoutSignIn.NeedsLogin, withoutSignIn.Username));
    }

    [Fact]
    public void A_profiles_account_is_the_username_it_carries_else_the_name_on_its_own_certificate()
    {
        var carried = ProfileStore.Preview("# OVPN_ACCESS_SERVER_USERNAME=alex\n" + _pki.Profile(_key));
        var certificate = ProfileStore.Preview(_pki.Profile(_key));
        var neither = ProfileStore.Preview(WithoutBlock(WithoutBlock(_pki.Profile(_key), "cert"), "key") + "\nauth-user-pass\n");

        Assert.Equal("alex", carried.Account);
        Assert.Equal("client", certificate.Account);
        Assert.Null(neither.Account);
    }

    [Fact]
    public void AProfileThisVersionCannotServeIsKeptWithTheReason()
    {
        var store = new ProfileStore(_directory, new FakeProtector(1));

        var entry = store.Import("work", WithoutBlock(_pki.Profile(_key), "ca"));

        Assert.Equal("the profile has no <ca>", entry.Unsupported);
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
