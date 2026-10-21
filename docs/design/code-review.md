# The full code review (2026-10-21)

**Why it exists.** TASKS §0 item 1, before anything is published from the library on NuGet; the owner: *"lets do a full
code codereview include docs with refactor (for better artitecture)"*.

**How it was made.** Seven read-only audits, one area each, on briefs in `local/codex/review-*-brief.md` — a shared
`review-common.md` first: AGENTS.md's rules, and three lists (defects, architecture, documents), each item with its
`path:line`, a failure scenario and a fix, and "could not check" where a static read cannot settle it. Codex CLI
0.162.0-alpha.17.2 (`codex exec -s read-only`) answered three — the library, `Sallyport.Windows`, and what runs
elevated (the service, setup, the uninstaller, the host, the release workflow) — and then ran out of credits; Claude
review agents answered the other four on the same briefs: the program's host, the window's page, the tests and tools,
and the documents. Their answers are kept, unedited, in `local/codex/review-*.md`.

Claude checked every finding against the code before acting on it — one, junction traversal, by a test of .NET's own
enumeration in a scratch folder — and the verdicts below are Claude's. Nothing here quotes a profile, a key or an
address of the owner's.

**Verdicts.** *Holds*: the code does what the finding says. *Partly*: it does, but less than said, or only where said.
*Does not hold*: it does not. **Dispositions**: *fixed* or *taken* (in the commits named at the end), *refused* (with
why), *open* (in TASKS, with why it waits).

## The library (`src/Sallyport`) — Codex

| # | Sev. | Finding | Verdict | Disposition |
|---|---|---|---|---|
| L1 | high | `verify-x509-name` (and fingerprint pins) ignored: a profile pinning its server accepts another of the same CA | holds | fixed (D35): `name`, `name-prefix` and `subject` checked; `peer-fingerprint`, `verify-hash` or `tls-verify` refuse the profile before any packet |
| L2 | high | the server's text (AUTH_FAILED, RESTART, a key exchange's bytes, a cipher name) copied whole into exceptions and status | partly: the server's own words, shown to the person — but a challenge (`CRV1`) or session text rides in them | fixed: quoted to 120 printable characters, never a challenge's state or a token (`ServerWords`) |
| L3 | high | TCP reassembly keeps overlapping early segments whole: a server through the proxy can make one connection hold gigabytes | holds | fixed: early bytes held count against the receive window |
| L4 | high | DNS answers taken from any source with a pending id; the question not checked | holds (inside the tunnel only) | fixed: from the server asked, naming the question asked |
| L5 | high | after a retransmit timeout `_sndNxt` rewinds, and the peer's ACK for data already sent is refused as "never sent": the connection stalls to a timeout | holds | fixed: the highest sequence sent is kept (`_sndMax`), an ACK up to it taken |
| L6 | high | `Tunnel.DisposeAsync` lets an exit notice's failure escape before anything is released | holds | fixed: the notices best-effort, the release in `finally` |
| L7 | high | the dispatcher's queue to the proxy is unbounded, so the tunnel's own bounded queue bounds nothing | holds | fixed: bounded (4,096, oldest dropped), counted (`DispatchCounters.Overflowed`) |
| L8 | medium | TCP takes an ACK before checking the segment's sequence | holds; an off-path sender inside the tunnel must guess the four-tuple and an in-window ACK | refused: RFC 5961's challenge ACKs are the remedy; the guess is 1 in 2^14 per port tried |
| L9 | medium | disposing a running `TunnelSession` reconnects it unless its caller also cancelled | holds | fixed: the session owns its lifetime; dispose cancels and waits for the run |
| L10 | medium | `ConnectAsync` spins for ever when all 8,192 proxy ports to one destination are taken | holds | fixed: each port tried once, then `TooManyOpenSockets` |
| L11 | medium | the first control packet pins the peer's session before its ACK is checked | holds | fixed: a packet acknowledging another session pins nothing |
| L12 | medium | a control packet past the receive window is acknowledged, then dropped | holds | fixed: acknowledged only when delivered, kept, or a repeat |
| L13 | medium | `TlsInbox`, `OtherMessages` and the control queue grow without bound | holds; the peer must hold the profile's tls-crypt key | fixed in part: the inbox refuses a message over 1 MiB, `OtherMessages` keeps the latest 64 (a later PUSH_REPLY redacted); the control queue left, for the same key |
| L14 | medium | a socket whose connect fails is never disposed | holds | fixed |
| L15 | medium | AUTH_FAILED in place of the key exchange reads as silence | holds; no server measured does this | fixed: recognised there, a typed refusal |
| L16 | medium | a truncated DNS answer is cached as "no such name" | holds | fixed: asked again, not cached |
| L17 | medium | an echo reply believed from any source | holds | fixed: only from the address pinged |
| L18 | medium | `PushOptions.Redact` misses an option with leading whitespace | holds | fixed |
| L19 | medium | `Remote` and `ProfilePreview` print host and username in their generated `ToString` | partly: neither is a secret, and the window shows both on purpose | refused: rule 2 is the repository's; a host name in a consumer's own console is theirs |
| L20 | medium | key material and profile bytes not cleared after use | partly: .NET cannot promise it (the collector copies) | fixed where a buffer is ours: the master secret, the key block, the pre-master, the PKCS#12 export, a profile's bytes |
| L21 | medium | `connect-timeout 2147483647` throws out of range before the handshake's own error path | holds | fixed: a profile's durations capped at a day; a timer that still cannot hold one is a profile refusal |
| L22 | medium | `GameFolder.Scan` skips folders it cannot read without saying so | holds | open (TASKS §2.8): Add an app would need a third answer to show |
| L23 | medium | a saved login that does not read is reported as "not saved" | holds | open (TASKS §2.8): no store has produced one; a third state needs the window's words |
| LB1 | arch. | a session's generation has no one owner | — | refused for now: the order of its events is what the client's tests and measurements rest on |
| LB2 | arch. | `Tunnel.cs` holds the handshake and the running tunnel | — | taken: `Protocol/TunnelHandshake.cs`, `Protocol/TlsInbox.cs` |
| LB3 | arch. | `TcpConnection` mixes reassembly and sending | — | refused for now: L3 and L5 changed it in place; split after the live run |
| LB4 | arch. | game discovery and the old game store are the Windows app's, in the NuGet package | — | taken: `Sallyport.Windows/Apps`; the observer `net10.0-windows`, the bridge referencing it |
| LB5 | arch. | measurement helpers in the public surface | — | open: TASKS §0 item 2 (the public API) |
| LC1–3 | docs | README's preflight; a listening proxy is not a working route; `OtherMessages` may hold a later push | hold | fixed: `src/Sallyport/README.md`, the members' comments |

## `Sallyport.Windows` — Codex

| # | Sev. | Finding | Verdict | Disposition |
|---|---|---|---|---|
| W1 | high | selection by executable path alone: one person can route another's programs | holds (product.md said it refuses this) | fixed (D35): a run selects only processes in its client's session |
| W2 | high | an 8-byte TCP segment from a routed program's peer throws in `Nat.Inbound` and ends the run — the game then going direct | holds (found by Claude and the tests audit too) | fixed: TCP needs its 20 bytes and a data offset inside the packet; outbound frames the same |
| W3 | high | a packet NAT'd for one generation can be labelled with the next | holds; one packet at a commit | fixed: the NAT carries its generation (`Nat.Generation`), the packet goes with the NAT that turned it |
| W4 | high | a mapping outlives its socket's owner: a selected program's peers reach a later unselected owner of its port | holds; within the 2-minute idle, only from peers the first program sent to | open (TASKS §2.10): measured in the long run with the owner tables' churn |
| W5 | high | a TCP four-tuple reused by another process inherits the old decision | partly: a SYN on a remembered tuple | fixed: a SYN is decided afresh |
| W6 | high | a UDP endpoint becoming shared keeps the first owner's decision | holds | open (TASKS §2.10): which owner wins is a policy, not a fix |
| W7 | high | unattributed TCP never reaches the coverage answer | partly: counted as unowned packets, never looked at again | fixed in part: unowned TCP is looked at again, and one later found selected counts as failed; one never found is not yet counted for the window — open (TASKS §2.8) |
| W8 | medium | the service log carries whole addresses (SYN misses, owner rows) | holds | fixed: public addresses masked `A.B.C.x` (`Addresses.Masked`, `Flow.ToString`) |
| W9 | medium | `ProgramSelection.Unreadable` read while the router writes it; the worker's report can die | holds | fixed |
| W10 | medium | Sallyport's own programs excluded by an unnormalised path (`sub\..\`) | holds | fixed: paths made whole first, one that is not a path dropped |
| W11 | medium | pieces held ahead cross generations | holds | fixed: a lane's held pieces go with its generation |
| W12 | medium | a final fragment of 1–7 bytes refused | holds | fixed |
| W13 | medium | inbound checksums recomputed, so a corrupted packet becomes valid | holds | fixed: a whole packet's transport checksum must hold before the rewrite (`BadChecksum`) |
| W14 | medium | an unselected UDP socket talking to many peers pins a decision per peer while it is open | holds | fixed: idle decisions not routed expire whether or not a socket holds their end |
| W15 | medium | cancelling a driver install lets go of the held installer while msiexec may still read it | holds | fixed: held until msiexec has exited |
| W16 | medium | `Interception` leaks its buffers when built or stopped badly | holds | fixed |
| W17 | medium | a partial control frame has no deadline | holds; only Sallyport's own client from the install folder gets that far, each on its own pipe instance | refused |
| W18 | medium | a packet frame under 5 bytes throws past the readers' handling | holds; only the attached client sends them | fixed: the frame reader refuses it |
| W19 | medium | an old Hold countdown can release a later outage | holds; a narrow race | fixed: the countdown names its outage |
| W20 | medium | `ServiceControl.Uninstall` ignores a stop that timed out | holds | fixed: it throws, `Stop` throws when it could not ask, and setup stops there (P6) |
| WB1 | arch. | lane state (generation, NAT, path) in two owners | — | taken in part: W3's NAT with its generation |
| WB2 | arch. | `Router` at 810 lines | — | taken: `Router.Counters.cs`, `Router.Pieces.cs` (partial; the same fields and locks) |
| WB3 | arch. | process identity read twice (`ProcessImage`, `PipePeer`) | — | refused for now: their access differs (a router's packet path, a pipe's peer) |
| WB4 | arch. | `ServiceControl` is registration and hosting | — | taken: `ServiceControl.Host.cs` (partial) |
| WB5 | arch. | `RouterProtocol.cs` is messages, framing and rules | — | taken: `RouterMessages.cs`, `FrameStream.cs` |
| WC1–6 | docs | README overstates coverage; product.md claims session isolation, retained handles, cleared probes, a richer framing | hold | fixed: README (what is and is not carried), product.md's and router.md's status |

## What runs elevated — Codex

| # | Sev. | Finding | Verdict | Disposition |
|---|---|---|---|---|
| P1 | high | elevated setup runs `Sallyport.Client.exe --exit` from a folder it has not yet protected | holds for a first install into a folder someone made beforehand | fixed: only the install the uninstall entry (HKLM) names |
| P2 | high | elevated removal and the rights reset follow junctions in user-writable data | holds — measured: .NET 10's recursive enumeration enters a junction | fixed (D35): `Installer.Within` never enters a reparse point; a link's rights never set |
| P3 | high | one person can route another's programs | holds (W1) | fixed (W1) |
| P4 | high | setup run again after an interrupted upgrade deletes `.old`, data and all | holds | fixed: an interrupted swap is put back first; a `.old` holding data is never deleted |
| P5 | high | a payload holding `data\` leaves the person's data in `.old` to be deleted | holds for `--payload` only | fixed: a payload may not name `data` |
| P6 | high | removal goes on after the service would not stop | holds | fixed: removal stops there, nothing else touched |
| P7 | high | cleanup paths given to `cmd.exe`, where `%NAME%` expands | holds for an install folder with `%` in it | fixed: such a folder refused |
| P8 | high | a release run again for an existing tag can attach another commit's build to it | holds | fixed: the workflow refuses a tag on another commit |
| P9 | medium | the service log carries whole addresses | holds (W8) | fixed (W8) |
| P10 | medium | a quiet update started inside the install returns 0 before the install ends | holds; by design (the folder cannot move while that process lives) | refused: README says so |
| P11 | medium | a cleanup after exit is reported as done | holds | open (TASKS §2.9): a removal to the end is not yet measured |
| P12 | medium | the driver's "Windows finishes it on restart" dropped | holds | fixed |
| P13 | medium | an unreadable setup record reads as "did not install the driver" | holds | open (TASKS §2.9) |
| P14 | medium | the worker's report reads a set the router writes | holds (W9) | fixed (W9) |
| P15 | medium | the workflow's actions pinned by movable tags, and a rehearsal holds write rights | holds | fixed in part: every action pinned by the commit its tag named on 2026-10-21; a read-only build job apart from the publishing one open (TASKS §6: it needs a dispatch to measure) |
| P16 | medium | the host's `stop` takes a closed port for a stopped process | holds | fixed: it waits for the instance's own signal to go |
| P17 | low | worker and enumerated `Process` objects not disposed | holds | fixed |
| P18 | low | `verify-release.ps1` takes a manifest or zip naming a path twice | holds | fixed (measured: refused a manifest naming one file twice; verified the 0.1.0 rehearsal folder as before) |
| PB1 | arch. | `Installer.cs` does the swap, the rights, the registration and the removal | — | taken: `Installer.Swap.cs`, `Installer.Rights.cs`, `Installer.Registration.cs`, `Installer.Removal.cs` (partial), linked whole into the uninstaller |
| PB2 | arch. | a run's lifetime spread over the supervisor's tasks | — | refused for now: unmeasured timing; P17 fixed in place |
| PB3 | arch. | the uninstaller compiles setup's files in | — | refused: one source, linked, is D32's choice and costs nothing |
| PB4 | arch. | two copies of `L` and `TextSize` | — | refused: setup's is compiled ahead of time and holds other state; the shared part is a dozen lines |
| PC1–5 | docs | session isolation, rollback, the guard's preflight, quiet exit codes, release assets | hold | fixed: product.md's status, README, CONTRIBUTING, release.yml's header |

## The program's host (`apps/Sallyport.Client`) — Claude

| # | Sev. | Finding | Verdict | Disposition |
|---|---|---|---|---|
| CA1 | high | Copy while another program holds the clipboard throws on a thread of its own and ends Sallyport | holds | fixed: asked again a few times, then said |
| CA2 | medium | the service manager's state read on the poll timer's thread without a catch: a refusal ends Sallyport | holds | fixed: a state not known; the view still shows it as installed and stopped — open (TASKS §2.8) |
| CA3 | medium | a damaged export gives "That did not work (FormatException)" | holds (the tests audit's A11 too) | fixed |
| CA4 | low | a profile link's redirect to another host, the review naming the typed one | holds; .NET strips the credentials on another host | fixed in part: the review names the host that answered; redirects still followed |
| CA5 | low | a library message reaching a notice could carry key material | does not hold: the library's messages carry the server's quoted words, cipher names and chain status, never a key | — |
| CB1 | arch. | `ClientCore` at 1,830 lines does five jobs | — | taken: `ClientCore.Profiles.cs`, `.Apps.cs`, `.Move.cs`, `.Driver.cs`, `.View.cs` (partial, every line as it was); a separate state builder not taken |
| CB2 | arch. | `Theme`, `TextSize`, `StartupRegistration` belong in `Sallyport.Windows` | — | refused: only this window uses them |
| CB3 | arch. | the client's and setup's folder rights built twice | — | refused: they differ on purpose (a person's folder against the shared areas); one helper would hide that |
| CB4 | arch. | the manifest's records beside `InstallLayout` | — | refused: setup reads no manifest in C# |
| CB5 | arch. | `Take` reads four stores under four locks | — | refused for now: display only, right at the next publish |
| CC1–3 | docs | a line count; `ChannelManager` in window.md; README's hosts | partly | fixed: window.md's status (the count was the brief's, not AGENTS.md's) |

## The window's page (`web/src`) — Claude

| # | Sev. | Finding | Verdict | Disposition |
|---|---|---|---|---|
| UA1 | high | an update's download that gets no answer leaves Settings "Downloading… 0%" until restart | holds | fixed: the update unavailable, said; any failure ends the download's phase |
| UA2 | high? | a link dropped on the window may navigate WebView2 to it, which then holds the state and can send intents | could not check (WebView2's and Shenora's own behaviour) | fixed regardless: the page refuses every drop but a file's; the host cancels any other navigation and every new window |
| UA3 | low | leaving Settings keeps an export or import open out of sight | holds | fixed |
| UA4 | low | the host takes Launch's profile and program, and a new app's profile, as the page sends them | holds | fixed |
| UA5 | low | a link's sign-in sent to an address that never asked | holds | fixed: only to the address that asked, for the page that asked |
| UA6 | low | the library's English reasons inside Chinese sentences | holds | fixed for unsupported profiles (`CannotConnect`); the store's and export's reasons open (TASKS §2.8) |
| UA7 | low | an app with no icon of its own shows another's as chosen | holds | open (TASKS §2.8) |
| UA8 | low | an app read, changed and put back outside the store's lock | holds | fixed: `RoutedItemStore.Update` |
| UA9 | low | a reloaded page leaves the host watching an app's programs | holds | fixed |
| UB1–9 | arch. | a contract drift test; `contract.ts` and `Editors.tsx` split; the subject's encoding; state the page derives; editor views in their own class; shared pieces | — | open (TASKS §2.8): the page has no tests but its type check, and its parts are measured through the window (UI Automation) |
| UC1–8 | docs | where it opens; the profile row; unsupported wording; `saving`; the empty Overview; app cards; visual.md; app ids | hold | fixed: the design records' status, the two comments; `saving` and the empty Overview open with UB |

## The tests and the tools — Claude

| # | Sev. | Finding | Verdict | Disposition |
|---|---|---|---|---|
| TA1 | med.–high | short TCP crashes the router | holds (W2) | fixed (W2), with tests |
| TA2 | high | the guard dies mid-outage when a remedy throws (an IOCTL refused, no installer) | holds | fixed: `Guard.cs`, each remedy said when it fails and the watch going on; whether it could remove the driver said at its start |
| TA3 | medium | the guard kills only `Sallyport.Driver` and says it killed what it could not | holds | fixed: the service's workers too, each said as killed, refused, or none |
| TA4 | medium | nothing that can cut the network checks that a guard runs | holds | open (TASKS §2.10): it changes how every measurement starts |
| TA5 | medium | `service-failure` looks for `Sallyport.Service` and fails falsely since D30 | holds | open (TASKS §2.10) |
| TA6 | medium | a firewall cut not made is reported as made | holds | fixed: said, and the measurement ends (exit 2) |
| TA7 | medium | the tools print public addresses whole | holds | fixed: masked (STUN, HTTPS echoes, ipinfo, the egress lookup) |
| TA8 | low–med. | `FakeSink` drained while a writer sends | holds | fixed |
| TA9 | low–med. | two assemblies' tests delete a shared temp parent | holds | fixed: a folder of their own each |
| TA10 | low | the supervisor's refusals and the worker's pid check untested | holds | open (TASKS §2.10): the service has no test project |
| TA11 | low | an export's damage reaches the person as a type name | holds (CA3) | fixed (CA3) |
| TA12 | low | the update test's "nothing left behind" cannot fail | holds | fixed: the bytes checked; a new test for a download with no answer |
| TA13 | low | the replay window's `Missing` and a forged high id untested | holds | open (TASKS §2.10) |
| TA14 | low | a language test returns before asserting on a Chinese Windows | holds | fixed: Windows' language set by the test |
| TA15 | low | the probe reports a timeout as failed | holds | fixed: unverifiable (2) |
| TA16 | low | `GameRoute` untested without a driver | holds | open (TASKS §2.10): needs a seam |
| TB1 | arch. | the Driver's 2,006-line `Program.cs` | — | taken in part: `Guard.cs`; the rest open (TASKS §2.10) |
| TB2 | arch. | the tools reach into the library's internals | — | open: TASKS §0 item 2 |
| TB3–7 | arch. | duplicated echo, STUN parsers, a router seam, identity checks in one place, shared test helpers | — | open (TASKS §2.10) |
| TC1–6 | docs | D13's guard; a stale remedy comment; the probe's own claim; the Driver's usage; temp folders; the service-failure record | hold | fixed: DECISIONS' index, `Guard.cs`, the probe's header, CONTRIBUTING; the usage text open |

## The documents — Claude

| # | Sev. | Finding | Verdict | Disposition |
|---|---|---|---|---|
| DA1 | high | README's list of hosts leaves out the guard's direct probes | holds | fixed, both READMEs |
| DA2 | high | README says all of a routed program's traffic; its DNS is Windows' and direct | holds | fixed, both READMEs |
| DA3 | high | product.md says its protocol and rights are as written | holds | fixed: its status; session isolation now true (W1) |
| DA4 | medium | the guard's parameters in product.md and game-routing.md | holds | fixed: the status notes |
| DA5 | medium | "administrator rights once, to install" | holds | fixed |
| DA6 | medium | the library README's preflight and its example without a login | holds | fixed |
| DA7 | medium | architecture.md false where it says it stands | holds | fixed: rewritten as the current architecture |
| DA8 | medium | game-routing.md not marked superseded | holds | fixed |
| DA9 | medium | CONTRIBUTING and release.yml: a release is one setup | holds | fixed |
| DA10–11 | medium | the design pages describe a window not built; the profile Edit flow before a2844e3 | holds | fixed: their status; DECISIONS' index |
| DA12 | low | README silent on an export without a password and the shared data folder | holds | fixed |
| DA13 | low | CHANGELOG 0.1.1's size and its unmeasured UAC claim | holds | fixed; the tray and Start-menu lines left as shipped |
| DA14 | low | username and password "supported", not measured against such a server | holds | fixed |
| DB1–8 | org. | one architecture page; status lines; a decisions index; the repository table; CHANGELOG's missing lines; the Chinese README's revision; the driverless READMEs; "CLAUDE.md rule" | — | taken but for DB5 (0.1.1 shipped as it was) |
| DC1–8 | rest | the package README; stale TASKS items; the driver installer's two places; SECURITY's scope; 1.1.1.1 as DNS; the probe's login; D23's text; D28's titles | hold | fixed but DC8 (history, left) |

## The commits

`aeeb9eb` the library · `993f49d` `Sallyport.Windows`, the service's protocol and the move of `GameFolder` ·
`c9f4e04` setup, the service, the host and the release · `e8f8312` the host, the page, the tools and the tests ·
`bf4a6e7` `ClientCore` split · the documents and this record with them. Tests: 499 before, 550 after, every one
passing.

## The live run

Pending: the installed copy (`D:\Program Files\Sallyport`) on this tree, jp connected, an app through it, the guard
beside it — TASKS §0 item 1's last condition. Its record goes here, and its numbers in TASKS-DONE.
