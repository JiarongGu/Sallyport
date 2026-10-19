# The client's window — design (2026-10-19)

**Why it exists.** The owner, 2026-10-19: *"lets make a better ui/ux design (you can use frameless version of shenora)
and use event messaging so ui just for display and also the design you can co work with codex (check other repo how
they work)"*.

**How it was made.** As in Kobo (`workings/kobo-as-an-application/codex-audit-2026-10-18.md` there): Codex CLI
0.162.0-alpha.17.2 (`codex exec -s read-only`, not on PATH), on the brief `local/codex/design-brief.md` with a screenshot
of the first window attached. A static read: Codex launched nothing and changed nothing. Its answer is below, from §1.

**Checked by Claude against the code the same day.** These hold:
- `TunnelSession`'s attempt count runs across the whole run and is never reset after a connection, so "N attempts since
  the connection was lost" needs the host to remember the count at the last up — as the design says.
- The proxy is CONNECT only (`Socks5Server`), so "proxy mode carries TCP" is true.
- `WaitUpAsync` throws on timeout and when the profile stops running, so a launch is never made late.
- Shenora 0.21.1 has what the window needs: `FramelessChrome` with `NativeCaptionButtons`, `WindowCommandModule`,
  `setCaptionButtons`, `startDrag`, `startResize`, `showSystemMenu`, `useWindowMaximized`, `post` (its own D23 makes
  `post` the default and `invoke` the exception), and `IColorScheme`.

Not in Shenora.Windows: the OS's resolved light or dark (only the Chromium shell reads it, internally) — the client
reads it itself; and changing a `TrayIcon`'s icon or text after it is made — the client keeps its own tray.

**Adopted with four changes.**
1. No `READY` command: Shenora's ready handshake already exists, and the host republishes the whole state on it.
2. No intent ids or session id: the page and the host live and die together, and the host turns a repeated intent away
   by what is already under way (a profile connecting, an app launching, a dialog open).
3. A deliberate disconnect returns a profile to "Not connected"; there is no separate "Disconnected" state to explain.
4. Info notices are expired by the host after eight seconds, not timed by the page, so a reload shows the same list.

---

**1. Principles**

1. **Answer the glance.** Show whether tunnels are connected, reconnecting or failed without opening details.
2. **Describe the boundary honestly.** “Connected” describes a tunnel. Starting a program with proxy settings does not prove that all its traffic uses them.
3. **Keep routine actions close.** Connect, disconnect and launch belong on their rows; configuration belongs in details.
4. **One authority.** Window and tray render the host’s decisions, labels and availability.
5. **Preserve uncertainty.** “Location unavailable” must never become “Connection failed.” Borrow this distinction, consistent controls and pushed updates from Kobo.

**2. Window anatomy**

All dimensions are device-independent pixels.

- **Default:** 960×680. **Minimum:** 760×520. Remember placement.
- **Title bar:** 40px; gate mark and “Sallyport” left, host-supplied summary toward the right, then three 46×40 native caption regions. Example: “1 connected · 1 reconnecting”.
- **Navigation:** 44px horizontal strip: “Profiles”, “Apps”, “How to use”. Selected item has an accent underline and stronger text.
- **Content:** one vertically scrolling region, 24px padding, maximum width 1040px. Page heading and primary action share a row.
- **Notices:** a reserved area below navigation, outside content scrolling. Latest notice visible; “Earlier notices (N)” expands the session’s retained notices.

Empty title-bar background starts `WindowCommands.startDrag()` on left mouse-down. Controls stop propagation. Double-click toggles maximize; right-click opens the system menu. A 6px top strip starts `startResize('top')`, excluding caption regions; disable it while maximized using `useWindowMaximized`.

Use `FramelessChrome`, `NativeCaptionButtons` and `WindowCommandModule`. Reserve the caption hole and report all three rectangles through `setCaptionButtons` on mount and layout/DPI changes. The host paints and operates them, preserving Snap Layouts. Establish matching host rectangles during splash.

At **760×520**, reduce content padding to 16px. Keep navigation labels intact. A collapsed profile occupies about 112px: two profiles and the beginning of a third remain visible beneath the header. Expanded details push subsequent rows downward. App editor fields stack. No horizontal scrolling or icon-only navigation.

**3. Screens and visible copy**

Text in braces is substituted by the host. Errors use sanitized host wording.

**Profiles**

Heading: **“Profiles”**  
Subtitle: “Connect a profile to give apps a local SOCKS5 proxy.”  
Action: **“Import profile…”**

Empty:

> **No profiles yet**  
> Import an OpenVPN profile from your VPN provider. Sallyport keeps an encrypted copy for your Windows account.  
> **Import profile…**

One profile uses the same row as several; it does not expand to fill the window. Keep import order stable during updates.

Each row contains name, status, primary action; below, **“SOCKS5 · 127.0.0.1:{port}”**, **“Copy address”**, and **“Details”**. Long names wrap. Transport badges move into details.

| Host phase | Status and supporting copy | Action |
|---|---|---|
| Idle | “Not connected” | “Connect” |
| Connecting | “Connecting…”; “Attempt {N}” | “Cancel” |
| Up | “Connected”; “Connected for {duration}” | “Disconnect” |
| Reconnecting | “Reconnecting…”; “{N} attempts since connection lost.” Then “Last attempt: {reason}” | “Disconnect” |
| Disconnecting | “Disconnecting…” | Disabled |
| Stopped normally | “Disconnected” | “Connect” |
| Stopped unexpectedly | “Connection stopped”; “{reason}” | “Try again” |
| Refused | “Could not connect”; “{reason}” | “Try again”, if host permits |
| Unsupported | “Unsupported profile”; “This version cannot connect this profile: {reason}” | Disabled “Connect” |
| Removing | “Removing profile…” | Disabled |

For retries before the first successful connection, say **“Retrying connection…”** and **“{N} attempts so far.”** The host tracks this distinction; raw runtime attempts are cumulative.

Details show “Transport”, “Imported”, “SOCKS5 port”, and, when supplied, “Tunnel address”, “Open connections”, “Downloaded”, “Uploaded”. Traffic is cumulative for the current run; omit charts.

Port is read-only until **“Change port”**. Editing shows `127.0.0.1:` beside a labelled text field, **“Save”**, **“Cancel”** and “Use a port from 1024 to 65535.” Save becomes “Saving…”. Host errors appear below the field, such as “Another profile uses this port.” While running, show “Disconnect before changing the port.” Never save on blur.

Location section:

- Unchecked: **“Check location”**; “Checks your exit IP using ipinfo.io through this tunnel.”
- Checking: **“Checking location…”**
- Checked: **“Exit location: {place}”**, IP, optional organisation, “Checked {time}”; **“Check again”**.
- Partial response: show available facts; missing place becomes “Location not provided.”
- Unavailable: “Location could not be checked. The lookup did not answer; the tunnel may still work.” **“Try again”**.
- Disconnected: “Connect to check location.”

Invalidate results on a new connection generation. During rechecking, label retained results **“Previous result”**. A late lookup must not populate a replacement connection.

Removal lives in details and requires disconnection. Confirmation:

> **Remove {name}?**  
> Deletes Sallyport’s encrypted copy. The original file stays unchanged. {N} app shortcuts will need another profile.  
> **Keep profile** · **Remove profile**

Omit the shortcut sentence when none are affected.

**Apps**

Heading: **“Apps”**  
Subtitle: “Launch apps with a profile’s proxy settings.”  
Action: **“Add app…”**

Persistent small note: “Games and programs that ignore proxy settings are not routed yet.”

Empty with profiles:

> **No apps yet**  
> Add a browser or proxy-aware program, then launch it here. Sallyport connects its profile first.  
> **Add app…**

Without profiles:

> **Import a profile first**  
> Apps need a profile to launch through.  
> **Import profile…**

Keep existing shortcuts visible if their profiles disappear.

Rows show app name, kind, **“Through {profile}”**, **“Launch”**, and **“Details”**. Details expose path, **“Edit”**, **“Startup settings”**, and **“Remove shortcut”**.

Editor labels: “Name”, “Profile”, “App kind”, “Extra arguments”; explicit “Save” and “Cancel”. Host guesses kind, but the person can correct it.

| Kind label | Explanation |
|---|---|
| “Chromium browser” | “Uses a separate browser profile. Your usual browser stays unchanged.” |
| “Firefox browser” | “Uses a separate browser profile with SOCKS5 and remote DNS.” |
| “Electron app” | “Quit the app completely, including its tray icon, before launching here. An existing instance may ignore these settings.” |
| “Proxy environment” | “Sets proxy environment variables. Only programs that honour them use the tunnel.” |

Startup settings display the host’s actual flags/preferences/environment, wrapping long lines.

Other states:

- Missing executable: **“App not found”**; “Remove this shortcut and add the app from its new location.” Launch disabled.
- Missing profile: **“Choose a profile before launching.”**
- Unsupported profile: **“This profile cannot connect in this version.”**
- Waiting: **“Connecting {profile} before launch…”**
- Starting: **“Starting…”**
- Success notice: **“{app} started with proxy settings for {profile}.”**
- Failed connection: **“{app} was not started. {reason}”**

Disable duplicate launches and editing during launch. The existing 45-second wait ends with a notice; never launch later after timeout or a deliberate disconnect. Do not show “Running” without process tracking. Removing a shortcut does not stop its program.

**How to use**

Keep this to three steps plus two short notes:

1. **“Import a profile.”** “Choose the OpenVPN profile supplied by your VPN provider.”
2. **“Add an app.”** “Choose its program file and profile.”
3. **“Launch here.”** “Sallyport connects the profile first. Other apps keep their normal connection.”

**“Manual setup”** expands to: “Choose SOCKS5, host 127.0.0.1, the profile’s port, and remote DNS where available.” Host supplies the selected profile’s endpoint and one copyable `curl --socks5-hostname` example.

Notes:

> “Proxy mode carries TCP. UDP traffic, including some voice and game traffic, is not routed.”

> “Closing this window keeps connections running in the tray. Choose ‘Exit Sallyport’ there to disconnect and quit.”

Remove the data-directory panel and broad privacy claims. Put browser/Electron explanations beside their relevant settings.

**4. Display and intent contract**

The contract as adopted is `apps/Sallyport.Client/web/src/contract.ts`; the host builds it in `ClientView.cs`. It is
Codex's proposal with the changes listed at the top of this page.

**5. Visual system**

Follow **system light/dark** through Shenora’s `ColorScheme`; a small utility should match its desktop. Style through `prefers-color-scheme`; host chrome and splash follow the same resolved scheme.

| Token | Dark | Light |
|---|---|---|
| Background | `#15171C` | `#F5F6F8` |
| Title/surface | `#1D2027` | `#FFFFFF` |
| Hover | `#2A303B` | `#E9EDF3` |
| Pressed | `#353D4B` | `#DCE3EC` |
| Text | `#E6E8EE` | `#192230` |
| Secondary | `#A9B1BF` | `#526071` |
| Control border | `#677487` | `#7A8797` |
| Accent/focus | `#5B9DFF` | `#185FC7` |

Dark state tokens retain tray meanings: off `#6B7280`, busy `#F2B84B`, up `#3CCF8E`, trouble `#F06A6A`. Light text variants: `#596474`, `#865900`, `#137347`, `#B42332`. Grey dots accompany readable secondary text.

Segoe UI Variable: heading 22/28 semibold; row title 16/22 semibold; body 14/20; metadata 12/18. Endpoints use Cascadia Mono and tabular numerals. Spacing: 4/8/12/16/24/32. Radii: controls 6px, rows 8px, pills fully rounded. Controls are at least 36px high.

Hover changes fill; focus uses a 2px accent outline with 2px offset. Motion is limited to 120ms colour changes and a busy spinner; reduced motion uses a static busy symbol.

Host `CaptionButtonColors`:

| Property | Dark | Light |
|---|---|---|
| Surface | `#1D2027` | `#FFFFFF` |
| Hover | `#2A303B` | `#E9EDF3` |
| Pressed | `#353D4B` | `#DCE3EC` |
| Glyph | `#E6E8EE` | `#192230` |
| CloseHover | `#C42B1C` | same |
| ClosePressed | `#A32417` | same |
| CloseGlyphHot | `#FFFFFF` | same |

**6. Components**

Use a small shared inventory: title bar; navigation item; page header; expandable profile row; app row; status pill; labelled port field; app editor; facts list; location result; removal confirmation; notice; empty/loading/error presentation.

Each interactive component supports default, hover, focus, disabled and busy. Rows additionally support expanded and blocked/error states. Details remain visual state; editing, confirmation and pending work come from the host.

**7. Tray**

Share the host’s aggregate summary and action permissions. Choose precedence **failure → busy → connected → off**, so a healthy tunnel cannot conceal another’s failure. Summary still reports mixed counts.

Menu: “Open Sallyport”; profile submenus with status and explicit Connect/Disconnect actions; “Launch app” submenu; “Exit Sallyport”. Disable unavailable launches with the same reasons. Tooltip: “Sallyport — {summary}”, truncated at a complete phrase within 127 characters.

Keep close-to-tray. First close notice: “Sallyport is still running in the tray. Connections stay up.” Exit disconnects all.

**8. Accessibility**

Tab order follows navigation, page action, then each row’s actions and expanded controls. Use native buttons, inputs and selects; navigation has `aria-current`, disclosures have `aria-expanded`, facts use definition lists.

Focus moves into an opened editor and returns to its trigger on cancellation. Removal confirmation traps focus, starts on “Keep profile”, and supports Escape. Status announcements are polite; new errors use alert semantics. Never announce byte-counter updates.

Meet 4.5:1 text and 3:1 control/focus contrast. Pair colours with words and symbols. Support 200% text scaling, wrapping reasons and paths without hiding actions. Preserve native caption and system-menu keyboard behaviour.

**9. Scope to refuse**

No maps, speedometers, connection scores, command palette, workshop panels or separate dashboard: they compete with the few decisions this utility needs.

No game-routing switches, “all traffic protected” claims, automatic Electron termination or universal kill-switch claims: current proxy mode cannot justify them.

No raw profile viewer, key display or protocol log console in this window. Names, safe reasons and effective startup settings provide the useful diagnostic surface.