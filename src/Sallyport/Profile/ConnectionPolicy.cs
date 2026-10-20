namespace Sallyport.Profile;

/// <summary>
/// How a session keeps its tunnel up: OpenVPN's reconnection directives, read from the profile and overridable by its
/// owner. A round tries every remote once; a round that connected nowhere is followed by a wait.
/// </summary>
/// <remarks>The meaning of each is the OpenVPN manual's, its directive named beside it; the defaults are this client's
/// where the manual's suit a game worse (a 300-second ceiling on the wait is long to sit out mid-game).</remarks>
public sealed record ConnectionPolicy
{
    /// <summary><c>connect-retry n</c>: the wait after a round that connected nowhere.</summary>
    public TimeSpan RetryWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary><c>connect-retry n max</c>: the longest that wait grows to. It doubles after each failed round from the
    /// fifth on, as the manual's "slowed down after 5 retries".</summary>
    public TimeSpan RetryWaitMax { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How far each wait strays either way, as a fraction of it, so clients a server dropped together do not
    /// all come back in the same second.</summary>
    public double Jitter { get; init; } = 0.1;

    /// <summary><c>connect-retry-max n</c>: how many rounds before the session gives up; null, never.</summary>
    public int? RoundsMax { get; init; }

    /// <summary><c>remote-random</c>: each round tries the remotes in a new random order, not as listed.</summary>
    public bool RandomOrder { get; init; }

    /// <summary>"udp" or "tcp": only the remotes of that transport are tried. Null, every remote as the profile lists it.
    /// A profile names a transport per remote (<c>proto</c>, or <c>remote host port proto</c>) and has no directive for
    /// one transport across all of them; this is its owner's choice, as OpenVPN Connect's "Protocol" setting is.</summary>
    public string? Protocol { get; init; }

    /// <summary><c>server-poll-timeout n</c> (or <c>connect-timeout n</c>): how long one remote has to answer.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary><c>auth-retry nointeract</c>: an authentication refusal is retried like any failure. Otherwise
    /// (<c>auth-retry none</c>, the manual's default) it ends the session — the same credentials will be refused again.</summary>
    public bool RetryAfterAuthFailure { get; init; }

    /// <summary><c>ping n</c> / <c>keepalive n m</c>: a keepalive every n seconds of silence, when the server pushes none.</summary>
    public int? Ping { get; init; } = 10;

    /// <summary><c>ping-restart m</c> / <c>keepalive n m</c>: the tunnel is dead after m seconds without a word from the
    /// server, when the server pushes no ping-restart of its own. Without one a silent server is noticed only when the
    /// transport fails, which a UDP socket never does.</summary>
    public int? PingRestart { get; init; } = 60;

    /// <summary><c>pull-filter ignore "ping"</c> / <c>"ping-restart"</c>: this side's keepalive outranks the server's.
    /// A server's ping-restart is how long a game's packets go into a dead tunnel before anything can act on it (50 s
    /// from the JP server, measured); a shorter one of the client's own notices sooner.</summary>
    public bool KeepaliveOverridesServer { get; init; }

    /// <summary>The wait before the round after <paramref name="failedRounds"/> rounds in a row connected nowhere, before
    /// <see cref="Jitter"/>.</summary>
    public TimeSpan WaitAfter(int failedRounds)
    {
        var doublings = Math.Clamp(failedRounds - 4, 0, 20);
        var ticks = RetryWait.Ticks * (1L << doublings);
        return TimeSpan.FromTicks(Math.Min(ticks, Math.Max(RetryWaitMax.Ticks, RetryWait.Ticks)));
    }

    /// <summary><see cref="WaitAfter(int)"/> moved by up to <see cref="Jitter"/> of itself, either way.</summary>
    public TimeSpan WaitAfter(int failedRounds, Random random)
    {
        var wait = WaitAfter(failedRounds);
        return TimeSpan.FromTicks((long)(wait.Ticks * (1 + Jitter * (random.NextDouble() * 2 - 1))));
    }
}
