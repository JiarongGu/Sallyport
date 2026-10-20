# The window — one Apps list, one channel per profile (2026-10-20)

**Why it exists.** The owner, 2026-10-20, after the Games page: *"isn't the game and app are the same thing? and we
better have a network view and a setting view tab"*, then *"because a game can also have multiple routing method, so we
basiclly need to support them all in the same channel"*. It replaces the window parts of `client-ui.md` and §5 of
`product.md` where they disagree.

**How it was made.** As `client-ui.md` was: Codex CLI (`codex exec -s read-only`) on the brief
`local/codex/window-brief.md` — a static read of the named files; Codex launched nothing and changed nothing. Its answer
is below the line, as it gave it.

**Checked by Claude against the code the same day.** These hold:
- `Connections` (proxy) and `GameRunner` (packet routing) each own a `TunnelSession`, and `ClientCore` refuses one profile
  to both (`PROFILE_IN_USE`) — the exclusivity the channel removes.
- Two readers of one tunnel cannot coexist: `TunnelNetwork` reads `Tunnel.Packets` itself (`Dispatch`), and so does the
  service's `RunPump` — hence one dispatcher per channel.
- The proxy's TCP ports and its resolver's UDP ports are each drawn at random from 49152–65535, independently — the
  range Windows hands programs, which the router's NAT keeps when it can.
- `SessionStatus` carries attempts and the last error but no next-attempt deadline, so a countdown needs new telemetry.
- `Socks5Server` is CONNECT only: the proxy method carries TCP, and its DNS goes through the resolver.

**Adopted with three changes.**
1. **The proxy's ports are 40960–49151; the router's NAT has the rest (1024–40959 and 49152–65535).** Windows gives
   programs ephemeral ports from 49152–65535 by default, so a game's own port is nearly always kept as it is (the NAT's
   port preservation, measured with Zenless), and the proxy's TCP and DNS sit below where Windows allocates.
2. **Pieces go to the router.** An inbound IPv4 fragment other than the first carries no ports; the proxy's TCP is
   MSS-clamped and never fragments, and its DNS answers are small, while the router already translates pieces
   (`Nat`). So the dispatcher sends every non-first piece to the router, which drops and counts one it cannot place,
   and needs no reassembly of its own.
3. **The Windows VPN method (D16) is left out.** It is not offered for a program, and step 8 below is dropped. It stays
   the driverless route kept outside the product (TASKS item 2).

---

**Use Apps for everything the person routes, and Network for what is happening now.** A saved app can contain several programs and several routing methods. Its VPN profile identifies one shared connection.

This is a proposed replacement for the relevant parts of [client-ui.md](client-ui.md) and [product.md](product.md). I read the named implementation files. No files were changed, and no application, test or installer was run. The architecture below still needs implementation and real-server measurement.

**One profile owns one channel.** Replace the separate session ownership in `Connections` and `GameRunner` with a client-side `ChannelManager`, keyed by stored profile ID. Each channel owns one `TunnelSession`, its proxy listener, diagnostics and generation dispatcher. Packet routing attaches to that channel; it never creates another login.

Exactly one dispatcher reads `Tunnel.Packets`. Refactor `TunnelNetwork` and `RunPump` to accept dispatched packets instead of reading the tunnel themselves. Both send through a bounded, generation-aware channel writer.

Divide the inner tunnel’s TCP and UDP port spaces identically:

| Owner | Range |
|---|---|
| Router NAT | 1024–57343 |
| Proxy TCP and proxy DNS | 57344–65535 |
| Reserved | 0–1023 |

The NAT preserves a program’s source port only when it lies within its allocation and is free for that protocol. Otherwise it translates into its range, including when Windows chose a port in the proxy range. Reverse translation restores the original endpoint. Exhaustion drops and counts traffic; it never borrows ports or silently sends directly. Proxy TCP allocation **and the resolver’s currently independent random UDP allocation** must use the proxy range.

For inbound TCP/UDP, dispatch by protocol and destination port, then validate the receiving component’s live mapping and peer. For ICMP errors, inspect the quoted outbound protocol and **source** port. Echo replies and errors quoting diagnostic echoes go to diagnostics by outstanding identifier/sequence. Malformed or unmatched packets are counted and discarded.

Fragments need bounded generation-scoped association/reassembly before a port can select their owner; noninitial fragments cannot be guessed. Coordinate outbound IPv4 fragment identifiers across producers so their datagrams cannot collide.

Extend the service protocol to negotiate this allocation. A versioned `PrepareChannel`/acknowledgement carries an opaque channel slot, generation, tunnel address, packet budget and both ranges. Commit follows successful validation; retirement invalidates queues, NAT, fragments and probes. Packet frames carry channel slot plus generation. No profile contents or credentials cross the pipe.

Remove all “one profile serves a game or a proxy” refusals. Adding either method to an established channel must not reconnect it. A failed proxy listener appears separately and does not make a working packet route fail.

**Keep one machine-wide interception run, containing multiple channels.** Retain one supervisor, lease and adapter-owning worker. Replace its single `GameRoute` generation with channel contexts. Each program selection names one channel and loss policy; different selections may use different profiles concurrently.

Each channel generation gets its own NAT, peer and fragment tables. Mapping ownership includes process-instance identity, avoiding reused-port attribution across programs. A channel reconnect retires only its mappings and reclassifies only its selections. Ending one app removes its selections; it does not reset interception underneath others. Reset adapters when the aggregate run ends or fails.

A program has one assigned profile. Methods selected for that program always use that profile’s channel. An app normally supplies one profile to all members; an advanced per-program override permits, for example, its launcher on another profile. Show that exception explicitly. Conflicting simultaneous assignments of the same executable are refused rather than resolved by list order.

Stopping packet routing releases its channel reference. Proxy launch enables the profile’s proxy until explicitly disconnected; Sallyport cannot infer its lifetime from an untracked launched process. Explicit **Disconnect profile** describes all affected selections and proxy clients before stopping them.

**The four tabs are Apps, Network, Profiles, Settings.** Move How to use inside Settings, with contextual links from empty and blocked states.

Keep the existing frameless chrome, 960×680 default, 760×520 minimum, system theme, native caption behavior and single content scroll. Cards retain their present visual language. Network uses stacked channel cards rather than a dense dashboard. At narrow widths, program editors and facts stack.

Ordinary opening starts on Apps. With no VPN profiles, first opening starts on Profiles with **Import profile…**, then offers **Add app…**. Adding an app remains possible before importing; activation explains the missing profile. Tray **Open Network** opens Network explicitly. Background status updates never move focus or change tabs.

**One saved model represents a program or a folder.** Call it `RoutedItem` internally and “app” in the window:

- Stable item ID, name, optional folder, creation time.
- Stable program IDs, exact executable paths, inclusion choice and its source.
- Item VPN profile and packet-loss defaults; optional program overrides.
- Per-program method set, proxy launch configuration, arguments and working directory.
- Optional folder launch target.

**Add app…** opens a host-held choice: **Choose a program…** or **Choose a folder…**. A folder scan lists executables with suggested inclusion and reasons. The person reviews methods and profile before saving. Preserve choices on rescan, preview additions and missing files, report **Scan incomplete**, and do not silently follow junctions. Allow **Add another program…**.

Each program offers:

| Method | Explanation and controls |
|---|---|
| **All traffic — packet driver** | Selected IPv4 TCP/UDP, subject to displayed restrictions and coverage. Include/leave out; VPN profile; loss behavior. |
| **Proxy settings when launched here** | Chromium, Firefox, Electron or proxy environment. TCP only; show family-specific help, arguments and actual startup settings. |
| **Selected destinations — Windows VPN** | Advanced driverless D16 fallback: explicit prefixes and exact app selection, with its measured limitations and external guard. |

Packet and proxy methods can both be selected for one executable. Its loopback connection to Sallyport’s SOCKS listener must remain usable; the proxy’s upstream traffic originates in Sallyport, which remains unselectable. Thus proxy-aware requests and other external program traffic share the channel without double interception.

D16 must relay packets to the same channel owner rather than retain a bridge-owned second tunnel. Refuse default routes, unsafe prefixes and conflicting Windows route ownership; do not combine driver and Windows interception for the same program. Its shared-channel integration and multi-profile behavior are unmeasured today. Keep that option unavailable until measured, with the reason shown.

Missing driver: retain selections and disable their **Route** action with **“Packet routing needs the driver. Open Settings to install it.”** Proxy-only launches remain available. Never automatically change the method. A mixed app’s launch requiring packet readiness is blocked until that dependency is resolved.

An Apps card shows name, the VPNs it goes through, method summary and status, and the routing check (three outcomes); no switch — an app is switched on in a VPN's section on Overview or on its page (D24); Launch only for an app Sallyport has to start, one with proxy settings (D22); Locate… for an app not where it was added from (D26). Its arrow opens **the app's page** (the owner: "a seprated page for game setup (more detail config and monitoring)"): its head again; **Monitoring** while it is routed — the run's detail, check and counts, and the traffic of its profile's tunnel, said to be all of that tunnel's; **App settings** under them (the owner: it was "still at bottom of the page") — Uses the VPN, If the VPN drops, Launch starts, Folder, with Edit beside the heading (name, icon, what Launch starts, If the VPN drops); **Goes through** — each VPN with its Use VPN switch; **Programs** — Add a program… and Scan folder again beside the heading, then each program on a line: its name and what it is doing ("Running · through jp", "Not running"), a switch for whether it goes through the VPN, and folded under it how it is routed, its connections now, what went through the VPN (the router's count), its file, Methods… and, for a proxy program, Launch; then Remove app. Its editors open there; the header's back arrow, or another page, leaves it and cancels an open edit.

**Add an app…** opens **Add an app**, a view of its own (D22): *Where is it?* — a path, or Browse… for a program or a folder — and what was found there; *About it* — name, VPN profile, when the VPN drops; *Its programs* for a folder, each with a switch; Add app, which opens the new app's page. **Route** arms packet selections without launching; once acknowledged, say **“Ready for launch.”** **Launch** uses the chosen folder target, arming required routing first. Cancellation or timeout permanently cancels that launch.

**Stop routing** leaves programs running. Proxy-only items have **Launch**, not a fictitious routing Stop. Mixed items expose both. Keep **Edit**, **Rescan folder**, **Remove app** and per-program launch actions. Configuration changes require affected routing to stop; attaching/detaching warns: **“Routing changes now. Your program may disconnect or need to rejoin.”**

**Use one versioned library store.** Generalize `GameProfileStore` into `RoutedItemStore`; retain a compatibility reader/API for existing callers. Client settings retain ports, connection overrides, startup and new-item defaults—not a second app store.

Migration combines existing game definitions, `GameChoice` and `RoutedApp`. Preserve game and proxy-app IDs, VPN IDs, executable choices, arguments, browser data locations and profile bindings. Accept both existing validated ID formats; allocate persistent program IDs. Do not automatically merge equal names or paths.

Write and read-verify the new snapshot before marking migration complete. Keep legacy data for recovery; make retries idempotent. Unreadable data produces **“Some apps could not be migrated”**, with a safe reason, rather than silently starting empty. Missing VPN references remain visible for repair.

**Network answers what is connected, routed, held or unchecked.** Its order is summary, channels, then routing infrastructure.

With nothing active: **“Nothing is connected”**, **“Route an app or connect a profile to begin.”**, with links to Apps and Profiles. Infrastructure health remains visible.

Each channel card shows profile, connection status, safe remote description, address, uptime, upload/download totals, proxy endpoint/listener state, open proxy connections and attached program selections. Proxy launches say **“Started with proxy settings”**, never “Running” or “All traffic routed” without corresponding evidence.

Packet sections show selected/unreadable processes, routed flows, packets out/handed up, held and direct while down, late flows, blocked traffic and stale-generation drops. Label held packets as discarded during the outage; imply no gameplay backlog.

Coverage is scoped to observed packet selections and interval:

- **Holds:** “No selected flow was observed going directly in the checked scope.”
- **Failed:** “{N} selected flows went directly.”
- **Unavailable:** “Coverage could not be checked: {reason}.”

Keep known failures visible alongside unreadable-process or adapter-gap uncertainty. Intentional direct fallback is displayed explicitly, not painted as successful VPN coverage. Disclose IPv6/LAN restrictions, system DNS and loopback boundaries.

Add scheduler telemetry: attempt total, attempts since last connection, server entry, failed rounds, next retry deadline and terminal reason. `SessionStatus` does not currently expose enough for a truthful countdown. Show **“Next attempt in {N} s”** only when a deadline exists. The hold-to-direct countdown starts once per outage, surviving failed attempts.

Offer selection **Stop routing**, channel **Retry now**, and profile **Disconnect**. Stopped retries under Hold leave healthy interception holding traffic: **“Connection needs attention. Selected traffic is held.”** A service failure uses verified recovery wording; otherwise **“Routing stopped. Recovery could not be checked.”**

**Measure latency through the dispatcher.** Extract and harden the console’s `TunnelEcho` into a bounded per-generation diagnostic component. Obtain the selected peer and passive TCP/UDP timing summaries from service telemetry; do not copy application payloads.

Proposed cadence: while Network is visible, paired direct/tunnel target probes and tunnel-gateway probes every five seconds; at most one bounded TTL sweep per minute, or on **Check now**, up to 24 hops and four outstanding probes per path. Use two-second timeouts. Cancel on retirement, target change or hidden window.

Show **Direct**, **Through VPN**, **Tunnel gateway**, target, sample count and timestamp. If only intermediate hops answer, label each path **“Farthest responding hop — estimate”**, including responder and TTL. Different responders are not a measured latency saving. Passive UDP timing says **“Time to next program reply.”** No responses: **“No reply — latency unavailable”**; no peer: **“Waiting for program traffic.”** Neither means tunnel failure.

**Settings contains configuration and help.** Its groups are:

- **Connection** (D21), for every profile, applied the moment it is chosen and from each profile's next connection: **VPN protocol** — Automatic (each profile's servers as it lists them), UDP or TCP — said as how Sallyport reaches the server, never as what goes through it; and **If it cannot connect** — Keep trying, or stop after 3, 5 or 10 failed rounds. One profile is changed on its own in a view of its own, **Connection settings for {profile}**, opened from its card's **Connection settings…**: the same two choices as radios, with initial/maximum retry wait, attempt timeout, silence detection, server order and authentication-refusal behaviour folded under **More settings**; what applies without changes there (Settings over the profile's own) beneath; Save / Cancel / **Follow Settings**; back is Cancel.
- **Packet routing:** driver and service health, versions, ownership, repair/install/remove availability and reboot requirement. Confirmation begins: **“This briefly interrupts this computer’s network. Measured here: about 2 seconds installing and 6 seconds removing; other computers may differ.”** Then explain elevation. Require packet selections stopped before removal.
- **Startup:** **Start Sallyport with Windows**, registering the current user’s `--tray` launch. This starts the client, not an old routing run.
- **Defaults for new apps:** VPN profile and packet-loss policy, including optional direct-after delay. Existing apps remain explicit.
- **How to use**, manual SOCKS5 setup with profile-specific endpoint and remote DNS guidance, and **About** with client/service/protocol versions.

Since D29 (`docs/design/profile-flow.md`): adding a profile is a page of its own — a file, a drop or a link, then the
profile looked at — and a profile's row has one action, **Edit…**, whose page holds its name, its sign-in, Connection
settings… and Remove profile; the row adds its server and, for one that signs in, its username. The rest stands:
Profiles is each profile's settings, not its connection (the owner: "we kind dont need the connect in profile page anymore"; it is Overview's switch and the tray's): a row each, nothing folded ("remove the dropdown for this and display some detail in the row") — its name and state, its proxy for other programs while connected, **Connection settings…** (the profile's own view, above) and **Remove profile** ("Disconnect it on Overview first." while it is connected); then **VPN protocol** and **If it cannot connect** in Settings' words, each "(as in Settings)" or "(this profile only)", **More settings: Changed for this profile** when it has other changes of its own, and **Imported**; the exit-location check while connected. Never its servers' "udp/1194, tcp/443": the owner read them as the only traffic it carries. The tunnel's own facts are Overview's one line under its graph (2026-10-21, the owner: "the entire "详情" collapse area should be removed … just display a line for them like connection number"): UDP or TCP — the type alone, *"isn't we just need to show udp/tcp instead?"* — its proxy, the connections open through it and its reconnects; the tunnel address and the gateway's ping (which the owner's server never answers) are not shown, and its round trip is a figure on the graph.

**Keep D15 throughout the contract.** Preserve `UiAction`, `Fact`, `EmptyView`, sequence ordering, notices and tokenized host-held editors/confirmations. Add host-authored navigation, field help and state labels; React must not derive permission, coverage or operational sentences from counters.

Use these C# record shapes, mirrored camelCase in `contract.ts`:

| Record | Fields |
|---|---|
| `ClientState` | Existing sequence/summary/profiles/editor/confirmation/notices; unified `Apps`; `Network`; `Settings`; `Navigation`; `InitialPage`; method/profile/kind/loss choices; revised top actions and empties. Remove `Games`, `GamesEmpty`, split `AppsNote`; move Guide/Driver under Settings/Network. |
| `RoutedItemView` | Id, name, folder, profileText, methodText, statusText, tone, programs, launchTarget, scan, packetSelectionIds, facts, actions. |
| `ProgramView` | Id, path, name, included, choiceSource, reason, profileId, methods, availability, proxy configuration, startupSettings, actions. |
| `SelectionView` | Id, itemId, programIds, channelId, phase, tone, statusText, detailText, since, lossText, directAt, counters, coverage, actions. |
| `ChannelView` | Id, profileId, phase, statusText, detailText, tone, connectedSince, facts, proxy, retry, selectionIds, latency, actions. |
| `CheckView` | Result, text, count, scope, checkedFrom/At, uncertainty facts. |
| `RetryView` | Attempt, attemptsSinceUp, round, roundLimit, serverEntry, nextAttemptAt, statusText, lastReason, actions. |
| `LatencyView` | Phase, statusText, targetText, direct/vpn/gateway samples, passive summaries, checkedAt, actions; samples include responder, TTL, milliseconds, count, estimate. |
| `RoutingHealthView` | Separate service/driver check result, phase, text, version, ownership, rebootRequired, checkedAt, actions. |
| `SettingsView` | Policy views, routing health, startup preference, defaults, guide, about. |
| `PolicyFieldView` | Id, label, ownValue, overrideValue, effectiveValue, sourceText, help, validation. |

Editors add typed item/program/method/launch/defaults values, revision and destination page. Saving revalidates revision, subjects and active selections; confirmations carry affected subjects. IDs, never arbitrary executable paths from the page, identify existing programs.

New enum copy is exhaustive:

| Enum | Values → visible words |
|---|---|
| `SelectionPhase` | preparing → “Preparing routing…”; connecting → “Connecting {profile}…”; armed → “Ready for launch”; routing → “Routing through {profile}”; held → “Reconnecting — selected traffic is held”; direct → “Reconnecting — selected traffic uses your own connection”; attention → wording above; stopping → “Stopping routing…”; stopped → “Routing stopped”; failed → “Could not start routing”; recoveryUnknown → wording above. |
| `LaunchPhase` | ready → “Ready to launch”; waiting → “Waiting for {profile}…”; starting → “Starting…”; started → “Started with proxy settings”; blocked → host-supplied reason. |
| `ProbePhase` | unchecked → “Not checked”; checking → “Checking latency…”; available → formatted sample; unavailable → wording above; stale → “Previous result”. |
| `HealthPhase` | checking → “Checking…”; ready → “Ready”; missing → “Not installed”; stopped → “Not running”; working → operation label; restartRequired → “Restart Windows to finish”; failed → “Check failed”; unknown → “Could not be checked”. |
| `ScanPhase` | complete → “Scan complete”; incomplete → “Scan incomplete”; missing → “Folder not found”; scanning → “Scanning folder…”. |

Retain existing profile phases, renaming their actions rather than inventing another connection lifecycle.

Intents: retain profile/policy/driver/copy/notice commands. Replace game commands with `ADD_APP {source}`, `RESCAN_APP`, `ADD_APP_PROGRAM`, `BEGIN_PROGRAM_EDIT`, `SAVE_APP`, `SAVE_PROGRAM`, `REQUEST_REMOVE_APP`, `BEGIN_ROUTE`, `START_ROUTE {token}`, `STOP_ROUTE {selectionId}`, `LAUNCH_APP {id,programId?}`. Add `RETRY_CHANNEL`, `CHECK_LATENCY`, `BEGIN_DEFAULTS_EDIT`, `SAVE_DEFAULTS`, `SET_START_WITH_WINDOWS`. Remove `RUN_GAME`, payloadless `STOP_RUN` and immediate `REMOVE_APP`. All edits use tokens; method changes are submitted editor values.

Tray: **Open Sallyport**, each profile a row that connects or disconnects it as pressed (its state on it; superseded the app and profile submenus, visual.md), **Settings**, **Exit Sallyport**. Render the same actions and reasons. Coverage failures, recovery uncertainty and direct fallback outrank healthy tunnels. Exit stops selections, disconnects channels, then quits.

**Build in working increments:**

1. Add typed views and four-tab navigation over existing behavior. C# view tests cover copy/actions; UI Automation checks navigation, focus and existing workflows.
2. Introduce the unified store and migration. Check preserved IDs/browser paths, restart idempotence, missing references and unreadable data.
3. Build dispatcher and port allocation behind existing controls. Test collision, ICMP quote, fragmentation, exhaustion and retirement; measure packet TCP/UDP plus proxy TCP/DNS simultaneously through one real-server login.
4. Move both connection owners into `ChannelManager`; remove exclusivity only after that measurement. Verify stopping one method leaves the other working.
5. Extend the single service run to multiple channels/selections. Measure concurrent profiles, equal generation numbers, one-channel outages, policy differences and final adapter recovery.
6. Enable mixed Apps editors and launch targets. UI Automation exercises add/edit/rescan, method changes, arming, cancelled launches and conflicts.
7. Add Network diagnostics and Settings/startup. Measure real outages, deadlines, latency replies/nonresponses and verified/unknown recovery.
8. Integrate D16’s shared-channel relay under its guard, then measure permitted combinations before enabling it.

Every step adds C# view checks and drives the real window through UI Automation. Record build/configuration revisions, sent/returned metadata, counters and cleanup in TASKS; record inaccessible dialogs or unsupported observations as **could not be checked**. Have someone other than the author check the result against the code before adoption, as AGENTS.md requires.