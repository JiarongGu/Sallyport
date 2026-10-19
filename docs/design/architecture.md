# Architecture (2026-10-19, revised)

What Sallyport is now, and what is still open. History: the first version of this page (approved 2026-10-19, D9–D11)
planned packet routing through WinDivert and a LocalSystem service; Defender blocked WinDivert (D10), Windows' own
per-app VPN was measured instead (D12) under a guard (D13), and with no privileged part left the client runs the runtime
itself (D14). Git has the earlier text.

## What the references do

Read for shape, never code (CLAUDE.md rule 1 binds OpenVPN's source; these are cited for structure only).

| Project | Its layers | What Sallyport takes |
|---|---|---|
| WireGuard for Windows | a service per tunnel · a manager service brokering for an unelevated UI | the UI never runs elevated; privileged work, if any, lives apart from it |
| Mullvad VPN | a platform core · a daemon with a management API · CLI and GUI as its clients · a WFP split-tunnel driver | route by executable; DNS made by the system's service cannot be attributed to an app |
| sing-box / Clash (mihomo) | inbounds (SOCKS, TUN) → rules → outbounds; GUIs drive the core through an API | inbound → router → outbound; process rules |
| Netch | a process mode on a packet driver; a TUN mode on Wintun | games ignore proxy settings: they are routed per process at the packet level, or not at all |
| Shenora (own) | one package per engine boundary | a package boundary only where a dependency or a platform differs |

Sources: [WireGuard for Windows](https://git.zx2c4.com/wireguard-windows/about/), [Mullvad win-split-tunnel](https://github.com/mullvad/win-split-tunnel),
[sing-box route rules](https://sing-box.sagernet.org/configuration/route/rule/), [Netch](https://github.com/NetchX/Netch).

## Packages and applications

```
src/
  Sallyport/                 the package everything uses        net10.0, BCL only (rule 4)
    Profile/                   .ovpn parsing                      public
    Protocol/                  control + data channel, transports internal — the wire is not an API
    Tunnel.cs                  IP packets in and out              public
    Net/                       userspace TCP + DNS over a tunnel  public
    Proxy/                     SOCKS5 inbound (CONNECT only)      public
    Runtime/                   TunnelSession (reconnect, remotes  public
                               in turn, status events),
                               TunnelService (a session behind a
                               loopback SOCKS port), ProfileStore
apps/
  Sallyport.Host/            Kobo's headless provider: serve|stop one profile on 127.0.0.1:1080, a status file
  Sallyport.Client/          the tray and window (Shenora 0.21.1, WebView2, frameless), unelevated
tools/
  Sallyport.Probe/           live measurements against a real server
tests/
```

`Sallyport.Windows` (a package that may carry a driver) does not exist: nothing needs one until game routing is
decided.

## The client

One process per signed-in user (a second start hands its arguments to the first and leaves).

- **Profiles** are imported as copies into `%LOCALAPPDATA%\Sallyport\profiles`, sealed with DPAPI for the user (D14).
  A profile is opened only to connect it.
- **Connections**: a `TunnelService` per connected profile, in this process, each on its own loopback SOCKS5 port
  (1088 upward; 1080 is the Host's). The port is bound only once the tunnel is up, so "the port answers" means "the
  route works". Exit tells each server.
- **Apps** are started through a profile's port, each family the way it listens (`AppLauncher`): Chromium browsers with
  a profile of their own and the proxy, host-resolver and WebRTC flags; Electron apps with the flags; Firefox with a
  profile whose `user.js` sets SOCKS5 and remote DNS; anything else with `ALL_PROXY`/`HTTPS_PROXY`/`HTTP_PROXY` as
  `socks5h://`.
- **The window is for display.** The host owns state and every decision: it pushes `CLIENT.STATE` (the whole view
  model) on the ready handshake and on every change, and `CLIENT.NOTICE` for each outcome; the page posts intents that
  answer nothing. The page's look is `docs/design/client-ui.md`.
- **The tray** shows the state in its icon's colour (grey nothing, amber connecting, green up, red failed) and offers
  each profile and app; it repeats a notice as a balloon only while the window is out of sight.

## Routing

**Proxy mode — what exists.** Applications that honour a proxy, through the SOCKS5 inbound and the userspace TCP. TCP
only: the proxy implements CONNECT, not UDP ASSOCIATE.

**Packet mode — open (D12, D13).** Games ignore proxies. Measured so far: WinDivert is blocked by Defender on download
(D10); Windows' per-app VPN runs a .NET 10 plug-in and confines a split route to one program, but a default route takes
the whole machine, and a program forced into the VPN cannot reach what the VPN does not route. The shapes left:

1. **Per-game server prefixes** (known, or learned from the game's own connections) as split routes with an app filter
   — what the measurements allow.
2. **Wintun** (WireGuard's signed adapter, redistributable with software that uses only its API) and routing by
   destination.
3. **A driver of our own**, signed — the cost of signing is the obstacle.

Whichever is chosen runs under D13's guard: nothing that can black-hole the machine is connected without a process
outside the VPN that probes the direct path and disconnects when it fails.

## Order of work

1. ~~Restructure~~, ~~runtime~~ (done).
2. **Client, proxy mode** — profiles, connections, apps, tray; measured against the real server.
3. **Decide game routing** among the three shapes above; measure it under the guard.
4. Phase 4 (`TASKS.md`), then release.
