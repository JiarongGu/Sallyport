# Sallyport

**Send the games and apps you choose through your OpenVPN server. Everything else keeps your own connection.**

[简体中文](README.zh-CN.md)

Sallyport is a free Windows client for OpenVPN servers that works app by app. Import the `.ovpn` profile your VPN
provider — or your own server — gave you, pick a game or a program, and turn on **Use VPN** for it. While that profile
is connected, that program's connections go through the VPN, TCP and UDP, from their first packet. Nothing else on
the computer does: your browser, your downloads and your other apps keep your own connection. (Its name lookups are
Windows' own, made for it outside the VPN: [How it works](#how-it-works).)

A sally port is a fortress's small, guarded gate: it lets chosen people through without opening the main gates.

Sallyport is independent and open source (MIT). It is written from the OpenVPN protocol's public description; it is
not OpenVPN Connect and is not affiliated with OpenVPN Inc.

## What it does

- **One VPN for some apps, not the whole computer.** A game goes through the VPN; the rest of the computer doesn't.
  Programs are matched by the program file, so a game's launcher, its helpers and the game itself can each be on or off.
- **Several VPNs at once.** Each profile is a section of the Overview with its own switch and its own apps. An app can be
  in more than one; while two are connected it goes through the first.
- **Stays connected.** It reconnects on its own, counting down to the next try. For each app you choose what happens
  while the VPN is down: its network waits for the VPN, it uses your own connection until the VPN is back, or it is
  taken off the VPN.
- **Profiles the way you have them.** Choose the file, drop it on the window, or get it from your provider's link (an
  OpenVPN Access Server address, signing in when it asks). Profiles that sign in with a username and password are
  supported (not yet tried against a server of that kind); the password is remembered only if you ask.
- **Browsers too.** A browser can be started with the profile's proxy instead, in a profile folder of its own.
- **See what it is doing.** Traffic and the VPN's ping on one graph, the exit location, and for each
  program whether it is running and going through the VPN.
- **Move to another computer.** Export your profiles, apps and settings to one file, with or without a password, and
  import them there. Apps installed somewhere else are found on the other drives, or located by you.
- **In English and 简体中文**, the window, the tray and setup alike.

## What you need

- Windows 10 version 1809 or later, or Windows 11, 64-bit (x64).
- Microsoft Edge WebView2 Runtime — part of Windows 11, and setup says so if it is missing.
- An OpenVPN profile (`.ovpn`) for a server whose profiles use **tls-crypt**, with AES-256-GCM or AES-128-GCM, signing in
  with a certificate, a username and password, or both. A profile that names its server (`verify-x509-name`) has that
  name checked. Profiles with tls-auth or tls-crypt-v2, or that check their server by fingerprint or by a script, are
  not supported yet: Sallyport says so when you import one, and never connects one with the check skipped.
- Administrator rights to install, to update and to remove Sallyport, and to install the packet driver: Windows asks
  each time.

## Install

1. Download `Sallyport-<version>-win-x64-setup.exe` from [Releases](../../releases). The release lists its SHA-256; to
   check your download, run `Get-FileHash .\Sallyport-<version>-win-x64-setup.exe` in PowerShell and compare.
2. Run it. This build is not code-signed yet, so Windows SmartScreen may say it protected your PC: choose **More info**,
   then **Run anyway**.
3. Choose where Sallyport goes (`C:\Program Files\Sallyport` unless you choose another folder; another drive works).
   Leave **the packet driver** ticked to send whole apps through the VPN — the network pauses for a few seconds while it
   installs.

Installing over an earlier version keeps your profiles, apps and settings. To install without the window:
`Sallyport-<version>-win-x64-setup.exe /quiet [--folder "D:\Program Files\Sallyport"] [--no-driver] [--language en|zh-Hans]`
answers by its exit code: 0 done, 1 failed, 2 bad arguments. (Run from inside an installed Sallyport's folder, as an update
is, it hands over to a copy of itself beside the folder and answers at once: the window says how it went.)

## Getting started

1. **Profiles → Import profile…** Choose your `.ovpn` file, drop it on the window, or paste your provider's link. Check
   its name and server, and sign in if it asks.
2. **Apps → Add an app…** Choose the game's folder, or the program. For a folder, Sallyport picks which of its programs
   use the VPN; you can change that.
3. **Overview.** In the profile's section, turn on **Use VPN** for the app, then switch the profile on to connect.
4. Start the game as you always do. While the profile is connected, its traffic goes through the VPN.

Closing the window keeps Sallyport running beside the clock, and what is connected stays connected. The tray icon
connects and disconnects your profiles; right-click it and choose **Exit Sallyport** to stop everything.

## How it works

The tunnel runs inside Sallyport itself: there is no virtual network adapter and no change to Windows' routes. The
**Sallyport service** uses NT Kernel's **Windows Packet Filter** driver to take the packets of the programs you chose and
send them through that program's profile; replies come back to the program as if it had its own connection. A routed
program's IPv6 connections and its traffic to your local network are blocked rather than leaked. Only the programs of
your own Windows session are routed: the same program run by another person on the computer keeps their connection.

What it does not carry: a program's **name lookups** are made for it by Windows (its DNS Client service), not by the
program, so they use your own connection and resolver — the names a game looks up are seen there, its traffic is not.
A connection whose program Windows had not yet named at its first packet goes direct; one later found to be a routed
program's is counted, and the app's page says so. A program started with proxy settings sends only TCP through the
VPN. Each connected profile also offers a SOCKS5 proxy on `127.0.0.1` for other programs to use — any program on the
computer can, as with any local proxy.

## Privacy and security

- Your profiles are kept **encrypted for your Windows account** (Windows' DPAPI) in the `data` folder of the install
  folder; the original `.ovpn` file is not needed afterwards. A password is kept the same way, and only when you choose
  **Remember password**. That folder is shared by everyone who uses Sallyport on the computer: another account can see
  your profiles' names, your apps and your settings, and could delete them, but cannot open a profile.
- An export made **without a password** holds your profiles, their keys included, as they are: keep it as you keep the
  `.ovpn` files themselves. With a password, they are sealed with it.
- Sallyport talks to nothing but your VPN server, except: NT Kernel's release on GitHub, to download the packet driver
  when you install it (checked against a pinned SHA-256); 1.1.1.1, through the VPN, every 30 seconds while a VPN is
  connected, to show its latency (and as the proxy's DNS server when your VPN server names none); while an app is routed,
  1.1.1.1 and 8.8.8.8 on port 443, **directly**, four times a second, by the Sallyport service — the check that your own
  connection still works, which ends routing if it stops (D13); ipinfo.io, through the VPN, when you check the exit
  location; GitHub's release API, once a day and when you check for updates, and GitHub's download of a newer Sallyport
  when you update; and a link you type to import a profile. There is no telemetry and no account.
- To report a security problem, see [SECURITY.md](SECURITY.md).

## Update

**Settings → About → Check for updates** says whether a newer Sallyport is out (Sallyport also looks once a day and
says so). **Update to …** downloads it, checks every byte against the release's manifest, and installs it: Windows asks
for administrator, and Sallyport closes for a few seconds and opens again. Your profiles, apps and settings are kept.

## Uninstall

**Start menu → Sallyport → Uninstall Sallyport**, or **Settings → About → Uninstall Sallyport…** in Sallyport, or Windows
**Settings → Apps → Installed apps → Sallyport → Uninstall**. Your profiles, apps and settings are kept unless you
choose to remove them too, and the packet driver is removed if Sallyport installed it.

## If something is not right

- **"Unavailable in this version"** — the profile uses something Sallyport does not support yet (tls-auth or
  tls-crypt-v2, for example). Its row says why, and a later version may connect it.
- **The app still uses my own connection** — check that **Use VPN** is on in a connected profile's section, and that
  Settings shows the packet driver and the Sallyport service as ready. Connections the program opened before you turned
  it on stay as they were: start it again.
- **The window is blank, or does not open** — install the WebView2 Runtime from Microsoft and start Sallyport again.
- **Where are my files?** In the install folder's `data` folder. Settings → **Move to another computer** exports them.

## The packet driver's licence

Sending whole apps through the VPN uses NT Kernel's **Windows Packet Filter** driver, which is free for personal,
educational and non-profit use. Sallyport does not include it: setup downloads NT Kernel's official, Microsoft-signed
release when you choose to install it. Using it commercially needs a licence from NT Kernel. Sallyport's own code is MIT.

## For developers

Building from source, the repository's layout, how changes are made and measured, and how releases are cut:
[CONTRIBUTING.md](CONTRIBUTING.md). The protocol library on its own: [src/Sallyport/README.md](src/Sallyport/README.md).
The design is in [docs/design](docs/design), every decision with its measurement in [docs/DECISIONS.md](docs/DECISIONS.md),
the work still open in [TASKS.md](TASKS.md), and what changed in [CHANGELOG.md](CHANGELOG.md).

## Licence and provenance

MIT ([LICENSE](LICENSE)). **Written from the OpenVPN protocol's public description, not from OpenVPN's source code** —
OpenVPN 2 is GPLv2 and OpenVPN 3 is AGPLv3, and neither's code is used, ported or translated here
([docs/DECISIONS.md](docs/DECISIONS.md), D1).

"OpenVPN" is a registered trademark of OpenVPN Inc. Sallyport is an independent, OpenVPN-*compatible* implementation
and is not affiliated with or endorsed by OpenVPN Inc. Windows Packet Filter is NT Kernel's.
