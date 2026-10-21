# The product — design (2026-10-20)

> **Since D30 (2026-10-21)** the window, the router service and setup are one program, `Sallyport.Client.exe`: the
> service is its `--service` mode, setup the release's file of it and its `/uninstall`. Where this page names them
> apart, they are its modes.
>
> **Status (2026-10-21, the code review):** the record of the design as adopted. The processes, pipes, rights and
> protocol now are `architecture.md`'s, and differ from this page's: one control pipe for every session and a pipe per
> run, not a pipe per session; the pipes' DACL gives interactive users read and write, not a logon SID; fourteen
> messages, not twenty, with no request ids, revisions or partial-frame deadlines; a peer is checked by the process id
> Windows gives at connection, its handle not kept; the guard probes every 250 ms with a 1 s connect, ends a run after
> 3 s dead with no preflight and no recovery check, and judges a router hung after 2 s still. An upgrade keeps the old
> folder only until the new one is in place (§6's rollback holds for the swap itself); an interrupted one is put back
> by the next run of setup. A run selects only its client's session's processes, as this page says — since the review.

**Why it exists.** The router works from a console tool (`tools/Sallyport.Driver route`, TASKS item 1, measured with
Zenless Zone Zero). The owner, asked what a game should do when the VPN drops mid-game: *"so there can be auto
reconnect mode with different config (this is more like openvpn) and please develop the entire application into a
full product"* — one install, a game profile and a VPN profile, Run; reconnecting on its own as OpenVPN does; the
driver managed by the app.

**How it was made.** As the earlier designs were: Codex CLI 0.162.0-alpha.17.2, `codex exec -s read-only`, on the brief
`local/codex/product-brief.md`. A static read; Codex changed and ran nothing. Its answer is below the line.

**Checked by Claude against the code the same night.** These hold:
- The client references `src/Sallyport` only; `TunnelSession` built a `TunnelNetwork` for every tunnel, the single
  reader of `Tunnel.Packets`; `PacketFilterSetup` runs msiexec with `runas` (its own prompt); `GameProfileStore`
  exists and nothing in the client uses it; no service, no client↔service IPC, no installer.
- Codex calls `Profile/ConnectionPolicy.cs` and the directive parsing "already in the tree": that was this work in
  progress, which it read half-done — not an earlier design.
- Codex proposes a folder path field because Shenora "may lack folder selection". It has one:
  `Shenora.Windows.FileDialogs.OpenFolderAsync` (0.21.1). The Games page uses it.
- The .NET 10 runtime ships `System.IO.Pipes.AccessControl`, `System.Security.AccessControl` and
  `System.Security.Principal.Windows`, not `ServiceBase`: the service calls the SCM API directly, as Codex says.

**Adopted with five changes, and two from building it.**
1. **Packets cross with a small header.** Codex frames every message with the service epoch, the Run and the
   generation (a 64-byte header). Control messages keep those identities; a packet frame carries its type, its
   generation and the packet — a game's small datagrams are most of the traffic.
2. **A peer is checked by account, session and a protected install path; release hashes wait for signing.** Codex
   also compares each peer's executable against the installed release's hashes. Until the binaries are signed, the
   install directory under `%ProgramFiles%` (writable by administrators only) is what an ordinary process cannot
   forge; the hash and publisher checks come with a certificate (TASKS, Ship).
3. **No transport reservation at first.** Codex has the client reserve its tunnel's outer endpoint with the service.
   The service refuses to select Sallyport's own programs, so the tunnel's socket is attributed to the client and
   goes direct — measured in the packet-pipe step; the reservation comes back only if that measurement fails.
4. **The keepalive keeps OpenVPN's precedence, override included.** A value the server pushes outranks the client's;
   the client's (from the profile or the person) applies when the server pushes none — or over the server's, when the
   profile or the person says so, as OpenVPN's `pull-filter ignore "ping-restart"` does. First adopted without the
   override; measured the same night (TASKS item 2, step 2): the server's `ping-restart 50` is 50 s of a game's packets
   into a dead tunnel before any loss policy can act, and the client's own 15 s noticed the loss in 15.0 s. So Codex's
   explicit override stands, under OpenVPN's name for it.
5. **WebView2 is checked, not installed.** Windows 11 carries it; Setup stops with a link where it is missing, rather
   than carrying Microsoft's installer.

6. **One control pipe, not one per session.** The supervisor creates `Sallyport.Router.v1` with
   `FILE_FLAG_FIRST_PIPE_INSTANCE` at its start — nothing else can hold the name before it — and a DACL that lets
   interactive users read and write it and denies the network; every client is then checked by its session, account
   and folder (change 2), and only one run exists on the machine. A run's own pipe is created by its worker, for the
   one client the run was prepared for. Simpler than a pipe per logon session, for the same refusals (measured, TASKS
   item 2 step 3).
7. **Ending a run resets its adapter, always.** The design leaves the adapter to the driver when the worker dies; the
   driver gives it back on its own only when the last handle to it closes (measured: a measuring process's handle
   held it intercepting, the machine's traffic queued 4.2 s). So the supervisor resets the run's adapter through a
   handle of its own whenever a run ends, and judges a worker hung by its router's drains — still for 1 s — before the
   guard's 3 s of a dead network.

Adopted as written, among the rest: one Run on the machine, a game profile paired with a VPN profile; a dormant
LocalSystem supervisor and a worker per Run, tied by a job object, the D13 guard in the supervisor; a pipe per session
with a logon-SID DACL; proxy mode and a game route exclusive for one VPN profile; the loss policy per game profile —
**Hold** by default, **Direct while reconnecting**, or **Stop**, and "direct after N seconds" under Hold; a generation
retired the moment its tunnel goes and the next committed before flows move; the Games page, the contract additions
and the copy; self-contained setup under `%ProgramFiles%`, the driver installed by the same elevated process; the
failure table; and the build steps of §8, each measured.

---

Sallyport should install once, keep its privileged routing machinery dormant until Run, and treat a Run as **one game profile paired with one VPN profile**. Closing the window preserves it in the tray; exiting the client ends it.

This is a proposed design, based on static reading. Nothing was changed, built, launched or installed, and no forbidden files or folders were opened. The existing measurements stand: Zenless’s 116 flows routed from launch, and two detach/attach cycles survived. Tunnel-loss recovery remains unmeasured.

**1. Processes, accounts and lifetimes**

| Component | Account and responsibility | Lifetime |
|---|---|---|
| Client | Unelevated interactive user; tray, window, sealed profiles, connection policy, tunnels, game configuration and launching | Login if enabled, otherwise manual launch; ends on Exit/logoff |
| Service supervisor | LocalSystem; authenticates clients, arbitrates the machine’s single Run, supervises worker, runs D13 guard | Automatic start at boot; dormant without Run |
| Routing worker | LocalSystem; owns interception handle, attribution, NAT and packet injection | Created for Run; destroyed on stop or failed supervision |
| Setup | Elevated only for installation, repair, upgrade or removal | One bounded transaction |

The supervisor holds a separate recovery handle. The worker owns every adapter mode it enables. A kill-on-close Job Object ties the worker to the supervisor; independent worker watchdog logic also expires its supervisor lease. These mechanisms require measurement together.

D13 lives **outside the worker**: direct-path preflight, probes every 250 ms with 500 ms deadlines, trip after three consecutive failures or four of six. Also monitor oldest queued packet and actual drain progress when work exists. On trip: kill worker, reset owned modes through the recovery handle if necessary, verify direct recovery for ten seconds, retain the outcome. Never uninstall the driver automatically to repair an ordinary network outage.

The service changes no machine routes or DNS settings. It intercepts supported adapters’ **send paths only**. Startup reconciles Sallyport-owned leftovers before accepting Run; encountering another interceptor’s configuration causes refusal.

Window close changes nothing. Exit stops routing before closing tunnels; process death or lease expiry provides the fallback. Logoff and switching away from the owning interactive session stop Run. Lock alone preserves it. Sleep preserves the person’s intent, but resume requires fresh adapter reconciliation, guard preflight and tunnel generation. Reboot never restores Run automatically.

Only one game Run exists machine-wide. Other sessions can use proxy mode, subject to loopback-port conflicts.

**2. Client–service protocol**

Use `\\.\pipe\Sallyport.Router.v1.s{sessionId}` for control. The supervisor creates endpoints from Windows session information. `Welcome` supplies a worker packet endpoint containing a fresh service epoch and Run ID.

DACL: SYSTEM full control; the intended **logon SID** only the specific read/write/synchronize rights needed, excluding pipe-instance creation. Reject remote clients; use first-instance protection. Endpoint squatting causes refusal, never connection to an alternate endpoint.

Both sides obtain peer PIDs from Windows, retain process handles, and verify creation time, token account/session, canonical executable path, protected installation directory and installed release hash. Client checks supervisor PID against SCM; it checks the worker against the authenticated supervisor’s child PID and creation time. Service verifies the client belongs to that logon session. Payload identity claims never establish authority.

There is no Sallyport signature today: protected installation plus hashes detects replacement by ordinary users, but does not establish publisher provenance or resist administrators. Later require the expected Authenticode publisher as well.

Framing: little-endian `u32 length`, then fixed header:

`major:u16, minor:u16, type:u16, flags:u16, requestId:u64, sequence:u64, serviceEpoch:128bit, runId:128bit, generation:u64, revision:u64`.

Control payloads use bounded JSON; packet payloads are binary. Maximum control frame 64 KiB; one IP packet per packet frame, maximum 65,535 bytes. Reject unsupported major versions, unknown mandatory features, invalid lengths, duplicate/nonmonotonic sequences and partial frames exceeding two seconds. Minor features require negotiation.

All messages are:

| Direction/message | Payload |
|---|---|
| C→S `Hello` | Supported versions, build ID, capabilities |
| S→C `Welcome` | Chosen version, epoch, capabilities, limits, current service/driver status |
| C→S `QueryStatus` | Empty |
| C→S `PrepareRun` | Included executable paths, selection revision, effective loss policy and fallback seconds |
| S→C `RunPrepared` | Run ID, worker identity, packet endpoint, adapter revision, capabilities |
| C→S `SetSelection` | Expected revision, replacement included paths; applied at an acknowledged barrier |
| C→S `ReserveTransport` | Attempt ID, protocol, exact local/remote endpoints |
| C→S `PrepareGeneration` | Generation, attempt ID, tunnel IPv4 address, effective MTU/MSS and overhead description |
| C→S `CommitGeneration` | Prepared generation and expected revision |
| C→S `GenerationDown` | Generation, sanitized reason code |
| C→S `Heartbeat` | Last received state sequence |
| C→S `StopRun` | Expected Run ID, reason |
| C→S `Recover` | Own Run ID only |
| S→C `Ack` / `Rejected` | Request ID, applied revision/generation or finite error code |
| S→C `State` | Phase, applied revisions, generation, lease deadline, adapter/driver status, guard result, loss deadline, reason |
| S→C `Counters` | Routed/direct/blocked/unattributed/late flows and packets, queue drops/age, mapping counts, injection refusals, attribution gaps, timing summaries |
| S→C `RunEnded` | Reason, final counters, direct-recovery check |
| S→C `OutboundPacket` | Packet sequence and NAT-translated IP bytes |
| C→S `InboundPacket` | Packet sequence and returned IP bytes |

Every Run message carries the header identities; generation zero means none. Heartbeat every second; proposed three-second lease. Values are acceptance-test targets.

Control and packet channels have independent readers. Each packet direction initially allows 4 MiB/2,048 queued packets and 250 ms maximum age. Coalesce telemetry first, discard expired diagnostics next, then drop/count selected data packets. Never block unrelated forwarding behind the pipe or release dropped selected packets directly. Control failure ends Run through fail-open recovery.

The service refuses cross-session processes, Sallyport infrastructure selection, stale commands, unsafe adapter coverage, unverified transport exemptions, arbitrary IOCTLs, launches, scripts and file operations. A returned packet must match its own current NAT mapping, authorized peer or validated fragment/ICMP association.

No VPN profile, private/static key or authentication material crosses this protocol. IP payloads can themselves contain application-sensitive data: never record them.

**3. Reconnection and Run behavior**

Store `ConnectionPolicy` per VPN profile, separately from its sealed profile. Settings show imported value, override and effective value, with **Save**, **Cancel**, **Use imported settings**. Changes apply next connection unless the person explicitly restarts.

Proposed defaults:

| Setting | Default |
|---|---|
| Retry delay | 2 seconds; fixed through five failures per entry, then doubles |
| Maximum delay / jitter | 30 seconds / ±10%; configurable, including fixed delay |
| Attempt limit | Unlimited; optional attempts per server entry |
| Attempt timeout | 30 seconds, including resolution, transport and authentication |
| Server order | As listed; optional randomized order per connection episode |
| Authentication refusal | Stop retries and require action |
| Keepalive fallback | Send after 10 seconds idle; declare dead after 60 seconds without authenticated reception |
| DNS resolution retry horizon | 60 seconds; configurable, including unlimited |

Success resets attempt limits. Retry state includes attempt, remote index, next deadline and sanitized cause. Cancellation interrupts resolution, handshake and waiting.

Directive seeds: `connect-retry` supplies delay/ceiling; `connect-retry-max` limits tries **per entry**; `remote-random` randomizes initial order; `connect-timeout` aliases `server-poll-timeout`; `resolv-retry` supplies resolution horizon. `auth-retry none/nointeract/interact` means stop/retry/request new credentials. `ping`, `ping-restart` and `keepalive` seed liveness; server-pushed values normally override local values. `persist-tun` preserves an interface, rather than defining a universal kill switch. These distinctions follow the [public OpenVPN manual](https://openvpn.net/community-docs/community-articles/openvpn-2-6-manual.html).

Sallyport’s defaults intentionally differ. Explicit keepalive overrides can supersede pushed values, with that precedence displayed. Zero disables a timer where supported. `interact` must remain visibly unsupported until credential entry exists; never silently convert it into endless retries.

Choose loss policy **per game profile**, with an explicit Run-only override: games differ in tolerance, and users should not repeatedly rediscover the setting.

- **Hold traffic** — default. Drop attributable selected traffic while reconnecting; retain no gameplay backlog.
- **Use direct connection while reconnecting** — keep selection active, pass eligible selected flows directly, move them back at the next committed generation.
- **Stop routing** — end Run and retries; restore direct traffic.
- Optional under Hold: **Use direct connection after N seconds**. Countdown starts once per outage, survives failed attempts, and resets only after routing becomes active.

`persist-tun` supports recommending Hold, but its absence never silently enables direct fallback.

Run transitions:

`Preparing → Connecting → Armed/Routing → ReconnectingHeld/ReconnectingDirect → Routing`

with branches to `AwaitingAction`, `Stopping`, `Stopped`, `GuardTripped`, `Failed`, or `RecoveryUnknown`.

Preparation arms selected-traffic blocking before a launch. Launch happens only after acknowledged routing readiness; a cancelled/timed-out launch never occurs later. Armed remains valid when no game process exists.

On tunnel loss, retire its generation immediately: discard queued packets, NAT mappings, fragment associations and outstanding probes. On replacement, prepare a fresh address, MTU, transport exemption and NAT table; acknowledge before committing. Reclassify existing selected flows at that barrier, including flows temporarily sent direct. Preserve cumulative evidence across generations.

TCP may reset or time out; UDP continuity is game/server dependent. Send-only interception cannot suppress late physical-path replies. Zenless’s measured migration is evidence for that game/build, not a seamless-reconnect promise.

When retries stop under Hold, keep blocking while client and worker remain healthy: **“Connection needs attention. Game traffic is held.”** Offer Retry and Stop. Router failure still restores direct traffic.

**4. Library changes**

The current tree already contains `Profile/ConnectionPolicy.cs` and partial directive parsing, beyond the brief’s baseline. `TunnelSession` still uses its existing options and unconditional `TunnelNetwork`; parsing alone establishes no reconnect behavior.

Replace that construction with an asynchronous per-generation consumer factory. Exactly one consumer owns `Tunnel.Packets`: `TunnelNetwork` for proxy mode, or the client’s packet pump for game mode. Add ordered generation-preparing/up/down/retired events, typed failure categories, policy scheduling and a transport-preparation callback.

Initially **proxy and game mode are exclusive for the same VPN profile**. Another profile may provide proxy service concurrently. Switching modes explicitly disconnects; no duplicate reader or hidden second login.

Parse scoped `<connection>` entries with inherited defaults, preserve their order and distinct identities, and reject unsupported contents rather than treating the block as opaque certificate-like text. Validate malformed directives without quoting their contents.

Move `TunnelEcho` and trace scheduling into a bounded diagnostic component sharing the generation’s sole dispatcher. Direct probes originate in the client; tunnel probes are consumed before forwarding returns to the service.

Expose transport/cipher-dependent overhead, advertised inner MTU and effective packet budget publicly. Do not hard-code 1448 for every transport. Retain measured fragment-by-fragment translation, whole oversized non-DF transmission, MSS clamping and DF errors.

Refactor Router into adapter interception, attribution/policy and replaceable generation forwarding. Support multiple adapter contexts, process-instance-aware mappings, mutable tunnel address, generation retirement, direct/hold transitions and explicit capture gaps.

**5. Client and display contract**

Add **Games** beside Profiles and Apps, using existing expandable cards, `Action`, `Fact`, tokenized editors/confirmations, focus handling and host-authored notices.

**Add game…** accepts a folder path. If Shenora lacks folder selection, provide **Folder path** plus **Choose a program…**, proposing its parent folder for confirmation. No new UI package is necessary.

Show each executable as **Included** or **Left out**, with default reason and user override. Support adding an outside executable, Rescan, Rename and Remove. Rescan preserves existing choices and previews additions/removals; inaccessible subfolders produce **“Scan incomplete”**. Do not silently follow junctions. Configuration changes take effect next Run initially.

Optional launch target includes executable, arguments and working directory, independently of routing inclusion. Launchers do not automatically admit descendants.

Run editor: **Game**, **VPN profile**, **When the connection drops**, and optional **Launch after routing is ready**. Without launching, arm for manual launch or attach to running instances. Before attaching or detaching, retain the adopted warning:

> Routing changes now. Your game may disconnect or need to rejoin.

Running view shows the pair, active processes, route state, loss behavior, attempt/countdown, routed and blocked flows, drops and three-outcome coverage:

- **Holds:** no observed selected flow escaped in the checked scope/interval.
- **Failed:** “{N} selected flows sent directly.”
- **Could not be checked:** unresolved ownership, unreadable processes or adapter gaps, with counts.

A known failure remains failed while additional uncertainty is shown separately. Selected IPv6/LAN blocks are visible restrictions, not successful routing. System DNS and loopback scope remain disclosed.

Ping shows **Direct**, **Through VPN**, sample count/time and target. A nonanswering game server yields **“Farthest responding hop — estimate”**. Passive UDP timing says **“Time to next game reply”**. No answer means unavailable; never invent a latency benefit.

Contract additions, mirrored in C# and TypeScript:

- `RunPhase`, `LossMode`, `DriverPhase`, `CheckResult { holds, failed, unavailable }`.
- `GameView(id,name,folder,programs,launchTarget,lossPolicy,facts,actions)`.
- `GameProgramView(path,included,reason,choiceSource,availability)`.
- `RunView(id,gameId,profileId,phase,tone,statusText,detailText,coverage,counters,pings,retry,actions)`.
- `CheckView(result,text,count,checkedAt,scope)`.
- `RetryView(attempt,limit,nextAttemptAt,text)`.
- `DriverView(phase,text,version,ownership,rebootRequired,checks,actions)`.
- `ConnectionPolicyView(values,effectiveValues,sources,actions)`.
- Add `games`, `gamesEmpty`, `run`, `driver`, `runEditor`, per-profile mode/policy and startup preference to `ClientState`.

Commands: `ADD_GAME`, `BEGIN_GAME_EDIT`, `SAVE_GAME`, `RESCAN_GAME`, `SET_GAME_PROGRAM`, `ADD_GAME_PROGRAM`, `REQUEST_REMOVE_GAME`, `BEGIN_RUN`, `START_RUN`, `LAUNCH_GAME`, `STOP_RUN`, `RETRY_RUN`, `CHECK_RUN_PING`, `BEGIN_POLICY_EDIT`, `SAVE_POLICY`, `RESET_POLICY`, `REQUEST_DRIVER_INSTALL`, `REQUEST_DRIVER_REMOVE`, `CONFIRM_ACTION`, `SET_START_WITH_WINDOWS`. IDs identify subjects; edits/confirmations use existing tokens. Extend notice subjects and empty-state actions accordingly.

Service authors routing facts; runtime authors tunnel facts; `ClientCore` reconciles them; `ClientView` authors every state-dependent word and permitted action. React posts intents only. IPC generations stay internal.

Changed copy elsewhere:

- Profiles subtitle: **“Use an OpenVPN profile for a game or a local SOCKS5 proxy.”**
- Connect becomes **“Connect proxy”**; game use shows **“In use by {game}”**, with unavailable proxy actions explained.
  *(Superseded by `window.md`: a profile's proxy and its routed apps share one channel, so Connect stays "Connect" and
  nothing is made unavailable by the other.)*
- Apps note: **“Apps here use proxy settings. Use Games for packet routing.”**
- Guide: Import profile → Add game or proxy-aware app → Run game or Launch app.
- Guide boundary: **“Proxy mode carries TCP. Games uses the packet driver for selected programs’ IPv4 TCP and UDP; unsupported traffic is shown as blocked or unchecked.”**
- Close notice: **“Sallyport is still running in the tray. Connections and game routing stay active.”**
- Exit tooltip: **“Stops game routing, disconnects profiles, then quits.”**
- Profile removal requires stopping its Run; summaries and choices describe mode without implying an active SOCKS endpoint.

Driver panel offers status, Install and Remove. Before either: **“This briefly interrupts this computer’s network. Measured here: about 2 seconds installing and 6 seconds removing; other computers may differ.”**

Tray adds current Run, Open Games, Stop routing and Retry. Direct fallback and coverage failures take precedence over healthy connections. First run offers profile import, game creation and driver setup; proxy use remains available without the driver.

**6. Installation, upgrade and removal**

Choose self-contained Windows x64 .NET 10 publishing. Keep Shenora’s existing application dependency; preflight WebView2 and install Microsoft’s pinned runtime if absent.

Install protected versioned files under `%ProgramFiles%\Sallyport`; protected setup journal, release hashes and driver MSI cache under `%ProgramData%\Sallyport`. User profiles/settings remain user-scoped and DPAPI-sealed.

> **Changed by the owner (2026-10-20):** the person chooses the install folder (`%ProgramFiles%\Sallyport` is only the
> default), and everything Sallyport writes for the machine — setup's record, the driver's installer, the service's
> logs — goes in `data` inside that folder, not in `%ProgramData%`. Setup gives the folder its own rights (owned by
> Administrators; SYSTEM and Administrators write, Users read and run; nothing inherited), since a folder off the
> system drive inherits rights that let users change it, and the service believes only clients run from it. Moving an
> installed Sallyport is removing it and installing it again. Each user's part (profiles, settings) moved there too,
> `data\users\<SID>`, and the driver's own files beside it: D19.

A BCL-only C# setup executable needs compression, HTTP, hashing, registry access and P/Invoke/COM for elevation, ACLs, Authenticode verification, SCM, Job Objects and Start-menu links. SCM hosting uses native APIs, not `ServiceBase`.

One elevated setup process performs all privileged operations. Refactor `PacketFilterSetup` so that process invokes `msiexec` directly, without nested `runas`. Fetch D18’s pinned official driver release and recheck it after protected staging.

Order: preflight/stage → verify → install files → install driver if requested → register/start dormant service → health check → publish shortcuts/uninstall entry. Journal each step; rollback removes only resources created by this transaction. Driver failure leaves proxy mode usable.

Register automatic service start and recovery restarts after 1/5/30 seconds; never resume an interrupted Run. Offer per-user Windows startup with `--tray`. Register `.ovpn` Open With/import handling without forcibly taking the default association.

Upgrade stages a complete new version, stops Runs/client/service, switches service path, checks health, then retires the previous version. Retain rollback until success. Reboot-required outcomes remain explicit.

Uninstall stops interception and verifies recovery first, then removes the service. Remove the driver only when Sallyport installed it and shared-use checks permit; otherwise retain it with a reason. Keep files needed for repair if driver removal fails. Remove application files, owned shortcuts/association entries/startup registration; delete user data only when explicitly chosen, using the unelevated user context. Never manually delete the inert driver file left by MSI.

Unsigned downloads may trigger SmartScreen; UAC shows an unknown publisher. Signing later establishes publisher identity and enables signature enforcement, but does not guarantee SmartScreen warnings disappear.

**7. Failure behavior**

| Failure | Packets/machine | Display |
|---|---|---|
| Client crash | Worker loses lease; interception ends; game becomes direct | Next start: interrupted Run, recovery result |
| Worker crash/hang | Supervisor kills/resets; measured handle-close recovery applies | “Routing stopped; traffic is direct” after verification |
| Supervisor crash/hang | Job closure or worker watchdog ends interception; client closes its lease | Service unavailable; recovery unknown until checked |
| Driver missing/removed | Refuse Run, or stop it; adapter rebinding may interrupt networking | Driver unavailable/restart required |
| Wi-Fi ↔ Ethernet | Existing contexts reconcile; new path may escape before capture | Reconnecting; coverage unavailable during gap |
| Sleep/resume | No old generation reused; reconcile and reconnect | Reconnecting; any enforcement gap disclosed |
| Tunnel drop: Hold | Selected packets dropped; unrelated traffic continues | Held, retry/countdown |
| Tunnel drop: Direct | Eligible selected traffic goes direct; returns on commit | Direct while reconnecting |
| Tunnel drop: Stop | Run ends; direct restored | Routing stopped |
| Hold deadline expires | Switch direct; continue tunnel retries | Direct fallback active |
| Windows update reboot | Networking follows Windows restart; no Run restored | Previous Run interrupted |
| Second session | Cannot acquire occupied Run; cannot select other session’s processes | “Game routing is in use by another session” |

Hold is a tunnel-loss policy, **not crash-proof confinement**. Simultaneous watchdog failure and kernel failure remain outside the guarantee.

**8. Build and measure, sequentially**

Each step records builds, configuration revision, sent/returned metadata, counters and cleanup in TASKS/implementation notes. Existing gates are evidence, not work to re-derive.

| Step | Send/exercise → required return |
|---|---|
| 1. Policy/parser | Generated profiles plus real-server failed remote, timeout and authentication refusal → correct attempts, delays, terminal outcomes and successful recovery |
| 2. Consumer generations | Real-server requests in proxy and packet-consumer modes; end/reopen tunnel → one reader, ordered retirement, fresh replies, stale results rejected |
| 3. Service skeleton | Authorized/unauthorized, stale and malformed pipe clients; kill/hang worker and supervisor → refusal or bounded cleanup, adapters direct |
| 4. Packet pipe | Existing selected TCP/UDP workload through real server, concurrent direct control, saturation → correct exits/replies and measured queue/latency cost |
| 5. Generation router | Drop tunnel under each policy; reconnect with same and changed address → expected hold/direct/stop, fresh mappings, no stale injection |
| 6. Adapter lifecycle | Both adapters active, handover, sleep, driver removal → packet-path evidence, honest gaps and independent machine recovery |
| 7. Zenless integration | Run from launch, attach/detach, real tunnel drop under each policy, sleep → game replies/session effects and coverage recorded |
| 8. Client workflow | Folder scan/rescan, overrides, cancelled launch, every failure state → host/window/tray agree with driver/server evidence |
| 9. Setup lifecycle | Clean VM install, denial, interrupted setup, upgrade rollback, reboot/removal → one elevation, usable networking, correct ownership/data retention |
| 10. Release endurance | Long real-server session, repeated outages, peer harness → bounded resources, NAT/reply evidence and documented unsupported behavior |

**9. Refusals and risks**

Refuse unconditional “every packet protected” claims while owner-table races and capture gaps remain; silent fallback; unsupported profiles reported as connected; unsolicited hosting without measurement; broad server-address exemptions; game injection or anti-cheat bypass; and commercial distribution beyond D18’s licensing scope.

Principal risks are attribution races, adapter coverage, transport-specific MTU behavior, pipe throughput, anti-cheat compatibility, server NAT and unsigned distribution. The intended product is complete only when its recovery policies are measured—not when its window can display them.