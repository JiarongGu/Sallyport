# Adding a VPN profile, signing in with it, and seeing it afterwards — design (2026-10-21, D29)

> **Status (2026-10-21, the code review):** built as below but for: a profile's row has Edit and Remove, and Edit holds
> its sign-in and how it connects — no separate connection-settings view, no Remove inside Edit; the Overview leaves
> out a profile this version cannot connect rather than show it as unavailable; there is no "Sign-in required" state.

**Why it exists.** The owner, 2026-10-20/21: *"import profile should be similar to how openvpn client does this (the
default name, the server detail, and etc)"*, *"it has different way to setup profile"*, *"a dragdrop profile into the
app also will be useful too"*, *"some profile need username password to run, some dont and each profile usually have
its username too"*, and *"you really need a good ui/ux design for this not just copy/paste from openvpn client (we need
to do its capability since its standard, but we will improve its ui/ux to fit our design, because this is more capable
rn)"*.

**How it was made.** Codex CLI 0.162.0-alpha.17.2 (`codex exec -s read-only`), on the brief
`local/codex/profile-flow-brief.md` with four screenshots of the window as it then was (no server names or accounts in
them; OpenVPN Connect's screens were described in words, not shown, since the owner's showed their own servers). Its
answer is below as it came, but for this header. Claude checked it against the code before building on it; the build
follows it except where this says otherwise:

- **Another edit open:** a profile opened or dropped then is refused with "Finish or cancel what you are editing first,
  then import the profile." rather than a discard confirmation — simpler, and nothing is lost.
- **Account on a Profiles row** (the owner, after: "imported profile should also show its account name"): every
  profile names whose it is — the username it signs in with ("Not set" when none is kept), else the username it carries
  or the name on its certificate — not "Not required"; one that names none says nothing of it.
- **No "Reading profile…" state:** a local file is read at once.
- **An app's Use VPN never connects** (D23), so it never asks for a sign-in; Connect on the Overview and the tray do.
- **A profile this version cannot connect** keeps the phase "Unavailable in this version" with one of three plain
  reasons (its security setup; a part missing; something else), not the library's own words.

---

**1. Getting a profile into Sallyport**

Keep the existing full-page setup view, cards and back arrow. All four entry points lead to the same review; nothing is saved or connected before confirmation. English and 简体中文 copy are paired below.

| State | On screen and actions |
|---|---|
| Open import | Title **“Import a VPN profile” / “导入 VPN 配置”**. Intro: **“Use the file or link you were given. Sallyport keeps an encrypted copy for your Windows account.” / “使用你收到的文件或链接。Sallyport 会为你的 Windows 账户保存一份加密副本。”** |
| File selected | Keep **“Where is your profile?” / “你的配置在哪里？”** and **“A file” / “文件”**, **“A link” / “链接”**. The file card becomes a drop zone: **“Drop your .ovpn file here” / “将 .ovpn 文件拖到这里”**, with **“Choose a file…” / “选择文件…”** opening the existing picker. **“Cancel” / “取消”** returns without saving. No disabled Import button before there is something to review. |
| Drag onto any page | While dragging, show one window-wide target: **“Drop to import a VPN profile” / “松开以导入 VPN 配置”**. Dropping one accepted file opens review. Leaving the window removes the target and preserves the page. |
| Read in progress | In the setup card: **“Reading profile…” / “正在读取配置…”**. Cancel remains available; cancelling prevents a late result from opening review. |
| Open from Explorer | Open or foreground the existing Sallyport window and enter the same review. Opening the file never silently imports or connects it. |
| Another edit is open | Before replacing its draft, reuse the confirmation dialog: **“Discard your changes?” / “放弃更改？”**; **“Importing this file will discard your unsaved changes.” / “导入此文件会放弃尚未保存的更改。”** Buttons: **“Keep editing” / “继续编辑”** and **“Discard and import” / “放弃并导入”**. Do not interrupt an active sign-in dialog; show **“Finish signing in before importing a profile.” / “请先完成登录，再导入配置。”** |
| Non-profile dropped | Keep the current page and draft. Notice: **“That is not an OpenVPN profile. Choose a .ovpn file.” / “这不是 OpenVPN 配置，请选择 .ovpn 文件。”** |
| Several files dropped or opened together | Accept none. Notice: **“Import one profile at a time.” / “请一次导入一个配置。”** This keeps review unambiguous, including mixed selections. |
| Unreadable file | In the source card: **“Could not read this file. Choose it again or use another file.” / “无法读取此文件，请重新选择或换一个文件。”** |
| File reads but is not a valid profile | **“This file does not contain a usable VPN profile. Ask whoever supplied it for another copy.” / “此文件中没有可用的 VPN 配置，请联系提供者重新获取。”** No review or Import action. |

The host receives dropped files directly. The page receives preview facts, never the profile text.

For **A link / 链接**, the same source card contains:

- **“Address or link” / “地址或链接”**, example `https://vpn.example.com`.
- Help: **“Enter your provider’s server address or a direct link to your profile.” / “输入服务商的服务器地址，或配置文件的直接链接。”**
- **“Get the profile” / “获取配置”** starts retrieval; it becomes **“Getting profile…” / “正在获取配置…”**. Cancel stops retrieval.
- A bare server name uses HTTPS and the Access Server retrieval flow. Explicit HTTP produces **“Use an https:// address or link.” / “请使用 https:// 地址或链接。”**
- If the provider requests credentials, reveal an inset group headed **“Sign in to {host}” / “登录 {host}”**, with **“Username” / “用户名”**, **“Password” / “密码”** and **“Sign in and get profile” / “登录并获取配置”**. Help: **“This sign-in downloads your profile. It does not connect the VPN.” / “此次登录用于下载配置，不会连接 VPN。”**
- Rejected provider sign-in: **“{host} did not accept that username and password.” / “{host} 不接受此用户名和密码。”** Keep username; clear password for retry.
- No response: **“Could not reach {host}. Try again.” / “无法连接 {host}，请重试。”** Keep the address; offer **“Try again” / “重试”**.
- Invalid HTTPS identity: **“Could not verify {host}’s identity. Check the address or contact your provider.” / “无法验证 {host} 的身份，请检查地址或联系服务商。”** No bypass button.
- A response without a profile: **“This address did not provide a VPN profile. Check it with your provider.” / “此地址未提供 VPN 配置，请向服务商确认地址。”**

Provider passwords are cleared after retrieval and never automatically become the VPN password.

**2. Reviewing and keeping it**

Replace the source-entry card with one review card. At 200%, this avoids leaving a large, finished source step above the important fields.

```text
← Import a VPN profile

┌ Review profile ──────────────────────────┐
│ Server           vpn.example.com         │
│ From             jp.ovpn                 │
│                                          │
│ Profile name     [jp                  ]  │
│                                          │
│ Sign-in          [only when needed]      │
│                                          │
│ □ Connect after import                   │
└──────────────────────────────────────────┘

[Import]   [Cancel]       Change source…
```

Copy:

| English | 简体中文 |
|---|---|
| Review profile | 确认配置 |
| Server | 服务器 |
| From | 来源 |
| Profile name | 配置名称 |
| Change source… | 更换来源… |
| Connect after import | 导入后立即连接 |
| Import | 导入 |
| Import and connect | 导入并连接 |

Server and source are facts, not editable inputs. Show every server hostname when there is more than one; wrap them. For a link, show the supplying host without query tokens or embedded credentials.

**Change source…** returns to the file/link choice. **Cancel**, Back or leaving the setup discards the unsaved import.

- **No username/password needed:** show no sign-in fields.
- **Username/password needed, with or without a client certificate:** show **“VPN sign-in” / “VPN 登录”**, Username and an unchecked **“Remember password” / “记住密码”**. Prefill a carried username; keep it editable. Otherwise suggest the provider username when available.
- With neither remembering nor connecting selected, no password field is needed. Help: **“You can sign in when you connect.” / “连接时再登录也可以。”** Import may save an incomplete username for later.
- Checking Remember password reveals Password. Checking Connect after import also reveals Password for this connection. Remember stays unchecked by default.
- Connecting or saving a password requires both fields. Inline errors: **“Enter a username.” / “请输入用户名。”** and **“Enter a password.” / “请输入密码。”**
- Connect after import starts unchecked. Selecting it changes the primary action to **Import and connect**. Import saves first; a connection failure leaves the saved profile available.

Reading a profile does not validate its login. Do not show a successful connection check during review.

**Unsupported profile:** keep review and Import available, with an amber notice:

**“You can import this profile, but this version cannot connect it.” / “可以导入此配置，但此版本暂时无法连接它。”**

Follow with a short, translated reason—for the screenshot’s case:

**“Its security setup is not supported yet.” / “暂不支持它的安全设置。”**

Omit credentials, disable Connect after import, and do not promise future support. Afterwards, both Profiles and Overview show **“Unavailable in this version” / “此版本暂不支持”**, with connection and app activation unavailable.

If support genuinely cannot be checked, say **“Connection support could not be checked.” / “无法确认此版本是否支持连接。”** Do not classify that as either supported or unsupported.

**Name collision:** suggest `jp (2)`, then `jp (3)`, before review. If the person edits it to an existing name, show **“A profile already uses this name. Choose another name.” / “已有同名配置，请换一个名称。”** Preserve every other field; never overwrite.

**After saving:** the first usable profile takes the person to Apps, with:

**“Imported {name}. Now add the game or program that should use it.” / “已导入 {name}，现在添加要使用它的游戏或程序。”**

Use the existing **“Add an app…” / “添加应用…”** action and preselect this profile in setup. If apps already exist, let the person assign them there. Later imports remain on Profiles, with **“Imported {name}.” / “已导入 {name}。”** Unsupported imports remain on Profiles with their warning.

**3. Signing in when connecting**

Use the existing dialog’s size, styling, focus handling and dimmed background, with a small credential form:

```text
Sign in to {name}

Server       vpn.example.com
Username     [alice                 ]
Password     [                      ]
□ Remember password

[Cancel]                     [Connect]
```

Title: **“Sign in to {name}” / “登录 {name}”**. Buttons: **“Cancel” / “取消”**, **“Connect” / “连接”**.

- **Overview switch:** open the dialog over Overview when credentials are missing.
- **Tray Connect:** foreground Sallyport on that profile’s Overview section and open the same dialog.
- **App’s Use VPN:** assigning the app with **“Use VPN” / “使用 VPN”** keeps its existing meaning. If that action initiates a connection, use the same dialog over the current page. If the profile is already connected, no prompt; if the switch only assigns the app, ask when the profile is connected.
- One profile gets one pending sign-in, even if several apps need it. Cancel cancels the connection request and any app activation waiting on that request; existing assignments remain.
- During submission use **“Connecting…” / “正在连接…”**. Only an established connection shows **“Connected” / “已连接”**. Cancel remains available.

**Remember password:** unchecked means the host keeps it only for the active connection and its reconnects; explicit disconnect or quitting clears it. Checked saves an encrypted password for this profile and Windows account. Saved passwords are never filled back into page inputs.

After `AUTH_FAILED`, stop automatic credential retries and return to the dialog with:

**“The VPN server did not accept this username and password. Check them and try again.” / “VPN 服务器不接受此用户名和密码，请检查后重试。”**

Keep username, clear the password field, and keep the Remember choice. Do not call a timeout or unreachable server a wrong password. A rejected saved password stops being retried automatically; it remains stored until replaced or forgotten.

Cancelling leaves the profile saved and disconnected, with **“Sign-in required” / “需要登录”**.

**4. Profiles and Edit**

Keep the unfolded card. Its only row action becomes **“Edit…” / “编辑…”**. Connecting stays on Overview and in the tray.

The row shows:

- Profile name and state.
- **Server / 服务器** and **Username / 用户名**. Missing required username: **“Not set” / “未填写”**; no username needed: **“Not required” / “无需用户名”**.
- **“VPN protocol” / “VPN 协议”** and **“If it cannot connect” / “无法连接时”**, using today’s effective values and **“(as in Settings)” / “（同‘设置’）”** or **“(this profile only)” / “（仅此配置）”**.
- **“Imported” / “导入时间”**. Keep the existing connected proxy fact when relevant. No row dropdown, connect toggle or separate red removal button.

Edit opens the existing full-page editor pattern, titled **“Edit {name}” / “编辑 {name}”**:

- Server remains a locked fact.
- Profile name is editable.
- Username is editable when required.
- **“Password” / “密码”** shows **“Saved” / “已保存”** or **“Not saved” / “未保存”**, never masking dots that imply a returned password.
- **“Change password…” / “更改密码…”** reveals an empty input and Remember password.
- **“Forget password” / “忘记密码”** marks the saved password for removal; **“Save” / “保存”** applies it. **Cancel** discards all pending changes.
- Changing username clears its associated saved password on Save unless a replacement is entered. Explain inline: **“Changing the username also forgets its saved password.” / “更改用户名也会清除原先保存的密码。”**
- Saving credentials does not disconnect a working session. After forgetting, that session may continue; the next explicit connection asks again.

Below the identity card, retain **“Connection settings…” / “连接设置…”** and **“Remove profile” / “移除配置”**. Connection settings opens today’s dedicated view; leaving an unsaved identity edit uses the discard confirmation.

Remove uses the existing confirmation dialog. While connected, keep it disabled with **“Disconnect it on Overview first.” / “请先在‘总览’中断开连接。”** The confirmation names affected apps and explains that removal clears this profile’s saved credentials; it does not delete the original supplied file.

**5. Default name and differences from Connect**

Use the **filename without `.ovpn`**: `jp.ovpn` becomes `jp`. For a direct link, use its filename; for provider retrieval without a filename, use the VPN server hostname. Apply the collision suffix before review.

That keeps Overview section titles short and recognisable. Server and username have their own facts; repeating them in `user@server [file]` makes every section harder to scan.

Sallyport retains Connect’s file/provider import, editable identity, remembered password and deferred sign-in. It differs through one shared review for every entry point, explicit download-versus-VPN sign-in, a quieter Profiles row, and a first-profile handoff to the person’s game.