# Sallyport

**让你选择的游戏和应用经由你的 OpenVPN 服务器，其他一切继续使用你自己的网络。**

[English](README.md)

Sallyport 是一款免费的 Windows OpenVPN 客户端，按应用分别工作。导入 VPN 服务商（或你自己的服务器）给你的 `.ovpn`
配置，选择一个游戏或程序，为它打开“使用 VPN”。配置连接期间，这个程序的全部流量——TCP 和 UDP，从第一个数据包起——
都经由 VPN；电脑上的其他一切（浏览器、下载、其他应用）继续使用你自己的网络。

Sallyport 是独立的开源软件（MIT 许可），依据 OpenVPN 协议的公开描述编写；它不是 OpenVPN Connect，与 OpenVPN Inc.
没有关联。

## 它能做什么

- **只让部分应用走 VPN，而不是整台电脑。** 游戏经由 VPN，电脑上的其他部分不受影响。按程序文件匹配，所以游戏的启动器、
  辅助程序和游戏本身可以分别打开或关闭。
- **同时使用多个 VPN。** 每个配置在“总览”中是一个独立的区域，有自己的开关和自己的应用。一个应用可以放进多个配置；
  两个都已连接时，它经由排在前面的那个。
- **保持连接。** 断开后会自动重连，并显示下次尝试的倒计时。每个应用都可以选择 VPN 断开期间怎么办：等待 VPN 恢复、
  期间使用你自己的网络，或为它关闭 VPN。
- **按你手上的方式导入配置。** 选择文件、拖到窗口上，或从服务商的链接获取（OpenVPN Access Server 地址，需要时登录）。
  支持需要用户名和密码登录的配置；只有你选择“记住密码”时才会保存密码。
- **也支持浏览器。** 浏览器可以改为以配置的代理启动，并使用它自己的浏览器资料。
- **看得到它在做什么。** 流量和 VPN 的延迟（在同一张图上）、出口位置，以及每个程序是否在运行、是否经由 VPN。
- **迁移到另一台电脑。** 把配置、应用和设置导出到一个文件（可设密码，也可不设），在另一台电脑上导入。安装位置不同的
  应用会在其他磁盘上自动查找，或由你指定位置。
- **支持 English 和 简体中文**，窗口、托盘和安装程序都一样。

## 需要什么

- Windows 10 1809 或更高版本，或 Windows 11，64 位（x64）。
- Microsoft Edge WebView2 Runtime——Windows 11 自带；如果缺少，安装程序会告诉你。
- 一个 OpenVPN 配置（`.ovpn`），其服务器使用 **tls-crypt** 和 AES-256-GCM 或 AES-128-GCM，以证书、用户名和密码，
  或两者登录。暂不支持使用 tls-auth 或 tls-crypt-v2 的配置：导入时 Sallyport 会告诉你。
- 安装时需要一次管理员权限。

## 安装

1. 从 [Releases](../../releases) 下载 `Sallyport-<版本>-win-x64-setup.exe`。发布说明中列出了它的 SHA-256；要核对，
   在 PowerShell 中运行 `Get-FileHash .\Sallyport-<版本>-win-x64-setup.exe` 并比较。
2. 运行它。此版本尚未进行代码签名，Windows SmartScreen 可能提示“已保护你的电脑”：选择“更多信息”，再选择“仍要运行”。
3. 选择安装位置（默认 `C:\Program Files\Sallyport`，也可以装到其他磁盘）。要让整个应用经由 VPN，请保持勾选数据包驱动——
   安装驱动时网络会暂停几秒钟。

在旧版本上安装会保留你的配置、应用和设置。无界面安装：
`Sallyport-<版本>-win-x64-setup.exe /quiet [--folder "D:\Program Files\Sallyport"] [--no-driver] [--language en|zh-Hans]`，
以退出码表示结果：0 完成，1 失败，2 参数错误。

## 开始使用

1. **配置 → 导入配置…** 选择你的 `.ovpn` 文件、把它拖到窗口上，或粘贴服务商的链接。确认它的名称和服务器，需要时登录。
2. **应用 → 添加应用…** 选择游戏的文件夹或程序。选择文件夹时，Sallyport 会挑出哪些程序使用 VPN，你可以修改。
3. **总览。** 在配置的区域里为应用打开“使用 VPN”，然后打开配置的开关进行连接。
4. 像平常一样启动游戏。配置连接期间，它的流量经由 VPN。

关闭窗口后，Sallyport 会在时钟旁继续运行，已连接的保持连接。托盘图标可以连接和断开配置；要全部停止，请右键单击它并
选择“退出 Sallyport”。

## 工作原理

隧道运行在 Sallyport 自身之中：没有虚拟网卡，也不修改 Windows 的路由。**Sallyport 服务**借助 NT Kernel 的
**Windows Packet Filter** 驱动，把你所选程序的数据包送入该程序所用配置的隧道；回应会像程序自己的连接一样返回给它。
经由 VPN 的程序，其 IPv6 流量和发往本机局域网的流量会被阻止，而不会泄露。以代理设置启动的程序只经由 VPN 发送 TCP。
每个已连接的配置还会在 `127.0.0.1` 上提供一个 SOCKS5 代理，供其他程序使用。

## 隐私与安全

- 你的配置**为你的 Windows 账户加密保存**（Windows DPAPI），位于安装文件夹的 `data` 文件夹中；之后不再需要原来的
  `.ovpn` 文件。密码以同样的方式保存，且只有在你选择“记住密码”时才保存。
- 除了你的 VPN 服务器，Sallyport 只会连接：安装数据包驱动时，从 GitHub 上 NT Kernel 的发布页下载驱动（并用固定的
  SHA-256 校验）；VPN 连接期间每 30 秒经由 VPN 连接 1.1.1.1，以显示延迟；检测出口位置时经由 VPN 连接 ipinfo.io；
  以及你为导入配置而输入的链接。
  没有遥测，也不需要账户。
- 报告安全问题，请见 [SECURITY.md](SECURITY.md)。

## 卸载

**开始菜单 → Sallyport → 卸载 Sallyport**，或在 Sallyport 中 **设置 → 关于 → 卸载 Sallyport…**，或 Windows
**设置 → 应用 → 已安装的应用 → Sallyport → 卸载**。除非你选择一并删除，否则会保留你的配置、应用和设置；
如果数据包驱动是 Sallyport 安装的，会将其移除。

## 遇到问题时

- **“此版本暂不支持”**——配置用到了 Sallyport 尚不支持的功能（例如 tls-auth 或 tls-crypt-v2）。它的那一行会说明原因，
  以后的版本可能可以连接它。
- **应用仍在使用我自己的网络**——检查它在已连接配置的区域里是否打开了“使用 VPN”，以及“设置”中数据包驱动和
  Sallyport 服务是否都已就绪。打开之前程序已建立的连接保持原样：请重新启动该程序。
- **窗口空白或打不开**——从 Microsoft 安装 WebView2 Runtime，然后重新启动 Sallyport。
- **我的文件在哪里？** 在安装文件夹的 `data` 文件夹中。“设置 → 迁移到另一台电脑”可以导出它们。

## 数据包驱动的许可

让整个应用经由 VPN 需要 NT Kernel 的 **Windows Packet Filter** 驱动，它可免费用于个人、教育和非营利用途。Sallyport
不包含该驱动：当你选择安装时，安装程序会下载 NT Kernel 官方发布、经 Microsoft 签名的版本。商业使用需要 NT Kernel 的
许可。Sallyport 自身的代码采用 MIT 许可。

## 开发者

从源代码构建、仓库结构、如何修改与测量、如何发布：[CONTRIBUTING.md](CONTRIBUTING.md)（英文）。

## 许可与来源

MIT（[LICENSE](LICENSE)）。**依据 OpenVPN 协议的公开描述编写，而非 OpenVPN 的源代码**——OpenVPN 2 采用 GPLv2，
OpenVPN 3 采用 AGPLv3，本项目未使用、移植或翻译两者的任何代码（[docs/DECISIONS.md](docs/DECISIONS.md)，D1）。

“OpenVPN”是 OpenVPN Inc. 的注册商标。Sallyport 是独立的、与 OpenVPN *兼容*的实现，与 OpenVPN Inc. 没有关联，也未获其
认可。Windows Packet Filter 归 NT Kernel 所有。
