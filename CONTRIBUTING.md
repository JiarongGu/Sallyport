# Contributing to Sallyport

Sallyport is an app-scoped, OpenVPN-compatible client in .NET: a protocol library, a Windows window and tray, a router
service, and a setup. This is how it is built, changed, measured and released. The rules in [AGENTS.md](AGENTS.md) bind
every change, by a person or a coding agent alike.

## The rules, in short

1. **Clean-room.** Never read, port or translate OpenVPN's source (OpenVPN 2 is GPLv2, OpenVPN 3 is AGPLv3; Sallyport is
   MIT). Work from the protocol's public description, the server's own behaviour and packet captures; what can only be
   learnt from their code is measured against a real server instead, and the measurement written down.
2. **No secret enters the repository.** A `.ovpn` profile holds a private key and a static key: it is read where its
   owner keeps it, never copied here, never logged, never printed — not even in a test failure. Tests make their own keys.
   Public addresses of your own (home, VPN server) are written masked, as `A.B.C.x`.
3. **Measured against a real server before it is called done.** A unit test proves the code agrees with itself; a server
   proves it agrees with OpenVPN. What was sent and what came back goes in the commit, `TASKS.md` or `docs/DECISIONS.md`.
4. **No runtime dependency beyond .NET's base library.** Cryptography and TLS are the BCL's. Adding a package is a decision
   recorded in `docs/DECISIONS.md` (the window's Shenora is D9).
5. **Comments say why, never what.** Where the wire format is the reason, name the field and its size.
6. **Three outcomes wherever something is checked:** it holds, it failed, or it could not be checked — never collapsed.

## What you need

- Windows 10 1809+ or Windows 11, x64 (the client, service and setup are Windows-only; the library is not).
- The [.NET 10 SDK](https://dotnet.microsoft.com/download).
- [Node.js](https://nodejs.org) 22 or later, for the window's page.
- For anything past unit tests: an OpenVPN server and a profile of your own, and administrator rights for the driver and
  the service.

## The repository

| Path | What it is |
|---|---|
| `src/Sallyport` | The protocol library: profiles, the control and data channels, the tunnel and its sessions, the userspace TCP/IP network, the SOCKS5 proxy, the profile store. Cross-platform. [Its README](src/Sallyport/README.md). |
| `src/Sallyport.Windows` | What only Windows has: the packet driver's management and interface, the router, the service's protocol, the install layout. |
| `apps/Sallyport.Client` | Sallyport's one program (D30): the window and the tray (WinForms + WebView2 through Shenora), and the host of the service and setup below. The host decides every word and action (`ClientCore`, `*View*.cs`); the page in `web/` (React, built into the executable) only shows them. |
| `apps/Sallyport.Service` | The router service (LocalSystem): the one part that opens the packet driver. A library the program runs as `--service`. |
| `apps/Sallyport.Setup` | The installer and uninstaller — a library the program runs under the release's name or with `/uninstall` — and the scripts that package a release (`package.ps1`, `version.ps1`) and check one (`verify-release.ps1`). |
| `apps/Sallyport.Uninstall` | The small uninstaller (D32): setup's removal compiled ahead of time, written beside the program as `uninstall.exe`. |
| `apps/Sallyport.Host` | A headless host: one profile's tunnel and its SOCKS5 proxy, with a status file, for another application to run. |
| `apps/Sallyport.Observer`, `apps/Sallyport.Vpn` | The driverless route (ETW observation, a Windows VPN plug-in): kept for if the driver were ever refused (D16), not in the product. |
| `tools/Sallyport.Probe` | Connects to a real server and says how far it got, phase by phase. |
| `tools/Sallyport.Driver` | Drives the packet driver and the router against a real server, and the guard that watches the network meanwhile. |
| `tests/` | One test project per area. No test needs a server, the driver or an account. |
| `docs/design/` | How the parts fit now (`architecture.md`), and the design records each part was built from. `docs/DECISIONS.md`: every decision, why, and what measured it. |
| `TASKS.md`, `CHANGELOG.md` | The work still open; what each version brings. |

## Build and test

```
dotnet build Sallyport.slnx
dotnet test Sallyport.slnx
```

The client's build makes its page the first time (`npm install`, `npm run build` in `apps/Sallyport.Client/web`, into
`wwwroot`, embedded). After changing the page, build it again there: `npm run build` (type-checks, then bundles).

Tests that need a temporary folder make one of their own and remove it. A test that listens on the network listens on
loopback only: one on every interface makes Windows Firewall ask, and a test run must never ask anything.

## Running and trying it

`dotnet run --project apps/Sallyport.Client` starts the window from source. A development build keeps its data in
`%LOCALAPPDATA%\Sallyport`, apart from an installed copy's. Routing apps needs the service and the driver, which setup
installs, and the service answers only programs in its own folder: try routing on an installed copy, from the release
setup or by publishing into the install folder.

Against a real server:

```
dotnet run --project tools/Sallyport.Probe -- <path to your profile.ovpn>
dotnet run --project tools/Sallyport.Probe -c Release -- <profile.ovpn> --concurrent 64
Sallyport.Driver guard --seconds 120          # from a copy outside the install folder, beside anything that touches the packet path
Sallyport.Driver route <profile.ovpn> --folder <game folder> --seconds 60
```

The probe takes no username and password: a profile that asks for them is measured through the window. What the tools
print is meant for a commit or TASKS (rule 3), so they mask the public addresses they are told (`A.B.C.x`); the
server's certificate subject they print is yours to mask before it is written down.

Nothing that can cut the computer off its network runs without the guard outside it (D13): it probes two outside
addresses twice a second, outside every interceptor, and says when the network stopped answering.

## Words and languages

Every word the person reads is translatable, in English in the code with Simplified Chinese beside it:

- the window's host: `L.T("…")` in C#, its Chinese in `apps/Sallyport.Client/Chinese.cs`;
- the page: `t('…')` in TypeScript, its Chinese in `apps/Sallyport.Client/web/src/i18n.ts`;
- setup: `L.T("…")`, its Chinese in `apps/Sallyport.Setup/Chinese.cs`.

A string with no Chinese fails `LanguageTests`. No letter-spacing on Chinese text.

## Making a change

Small commits, each with what it changes and why; for anything a server decides, what was measured. A decision that
shapes later work gets an entry in `docs/DECISIONS.md` (Dnn: what was decided, why, what measured it, what was not
checked); a change a person would notice gets a line under `## Unreleased` in `CHANGELOG.md`. All tests pass before a
commit. Pull requests say how the change was tried.

## Releases

A release is the setup and its SHA-256 — Sallyport's one program under the setup's name, which installs itself (D30) —
with the installed folder as a zip and `manifest.json`, each file's size and SHA-256 (D33), which every installed copy's
update reads (D34): a release without it is one no installed copy can update to.

```
powershell -File apps\Sallyport.Setup\version.ps1 -Bump patch     # writes the version and dates its CHANGELOG section
powershell -File apps\Sallyport.Setup\package.ps1                  # artifacts\release\ and artifacts\release-notes.md
```

`package.ps1` refuses a version whose CHANGELOG section is missing or empty, and always rebuilds the page from the tree.
On GitHub, the **Release** workflow (Actions → Release → Run workflow) does the same on a hosted Windows runner — every
test in Release, the package kept as the run's artifact — and, unless it is a rehearsal (`dry_run`, the default),
commits the version, tags it and drafts the release with the setup, its `.sha256`, the zip, `manifest.json` and the
notes; it refuses to release on a tag that names another commit, and runs each action by a pinned commit. Rehearse first,
install the rehearsal's setup, then run it for real.

### The library's package

The library (`src/Sallyport`) is also a NuGet package, with a version of its own in its project, its own
`src/Sallyport/CHANGELOG.md`, and its own workflow, **NuGet** (`.github/workflows/nuget.yml`, D36): the library's tests
on Linux and Windows, `dotnet pack` (the package and its symbols, kept as the run's artifact), and — unless it is a
rehearsal — the push to nuget.org by Trusted Publishing (no API key; nuget.org's policy names `nuget.yml`), the version
committed and the tag `nuget-v<version>`. It never makes a GitHub release: every installed app updates from the latest
one. Locally:

```
powershell -File apps\Sallyport.Setup\version.ps1 -VersionFile src\Sallyport\Sallyport.csproj -ChangelogFile src\Sallyport\CHANGELOG.md -TagPrefix nuget-v -Bump minor
dotnet pack src\Sallyport -c Release -o artifacts\nuget
```

Every public member of the library carries an XML comment (the build fails one without), and the library stays
reflection-free: it is marked trimmable and ahead-of-time compatible, and the analyzers fail what is not.

## Working with coding agents

[AGENTS.md](AGENTS.md) is the brief every agent works from (Claude Code reads it through `CLAUDE.md`; Codex reads it as
it is). It adds to the rules above that an agent never opens a profile or anything in Sallyport's data folders — the
client does that, not an agent — and how a second agent's work is checked before it is relied on.
