// The page's own words — headings, labels, help that does not depend on state — in the language the host says is in
// force (ClientState.language). Everything that depends on state arrives already in it, written by the host (D15).
// Each is written in English where it is used and looked up by that English; "{0}" marks where a value goes.

let language = 'en';

/** Set from each state, before it renders. */
export function setLanguage(next: string) {
  language = next;
  document.documentElement.lang = next;
}

/** What dates and times are written in: the language's own, or Windows' when it is English. */
export function locale(): string | undefined {
  return language === 'zh-Hans' ? 'zh-CN' : undefined;
}

export function t(english: string, ...values: (string | number)[]): string {
  const template = language === 'zh-Hans' ? chinese[english] ?? english : english;
  return values.length === 0 ? template : template.replace(/\{(\d+)\}/g, (_, i: string) => String(values[Number(i)]));
}

/** Every key the page uses, in Chinese. LanguageTests (Sallyport.Client.Tests) checks none is missing. */
const chinese: Record<string, string> = {
  // the shell
  'Main': '主菜单',
  'Apps': '应用',
  'Network': '网络',
  'Profiles': '配置',
  'Settings': '设置',
  'Loading…': '正在加载…',
  'Notices': '通知',
  'Earlier notices ({0})': '更早的通知（{0}）',
  'Minimize': '最小化',
  'Close': '关闭',
  'Details': '详情',
  // apps
  'Programs and games routed through a VPN profile, by all their traffic or by proxy settings at launch. Everything else keeps your own connection.':
    '经由 VPN 配置分流的程序和游戏：转发其全部流量，或在启动时使用代理设置。其他一切保持使用你自己的网络。',
  'Routed for {0}': '已分流 {0}',
  'Programs': '程序',
  ' · Launch starts this': ' · “启动”运行此程序',
  'Not found where it was added from': '在添加时的位置找不到',
  'Startup settings': '启动设置',
  'Routing': '分流',
  // network
  'What is connected, and what goes through it now.': '当前有哪些连接，以及经由它们的流量。',
  'Open Apps': '打开应用',
  'Open Profiles': '打开配置',
  'Routing on this computer': '此电脑上的分流',
  'Open Settings': '打开设置',
  'Connected for {0}': '已连接 {0}',
  'Latency': '延迟',
  'Checked': '检测于',
  '{0}s': '{0} 秒',
  '{0}m {1}s': '{0} 分 {1} 秒',
  '{0}h {1}m {2}s': '{0} 小时 {1} 分 {2} 秒',
  'Next attempt in {0} s': '{0} 秒后再次尝试',
  // profiles
  'Your OpenVPN profiles. Connect one to use it as a local SOCKS5 proxy; routing an app connects its profile itself.':
    '你的 OpenVPN 配置。连接一个配置即可将其用作本地 SOCKS5 代理；分流应用时会自动连接它的配置。',
  'SOCKS5': 'SOCKS5',
  'Exit location': '出口位置',
  'Previous result': '上一次的结果',
  // the window's frame and the Network page's connection
  'Overview': '总览',
  'Connection': '连接',
  'Profile': '配置',
  'Also connected': '其他连接',
  'Apps through it': '经由它的应用',
  'Traffic': '流量',
  'Show': '显示',
  // settings
  'General': '常规',
  'Language': '语言',
  'The words of this window, the tray and setup.': '此窗口、托盘和安装程序所用的语言。',
  'Packet routing': '数据包分流',
  'Routing a program’s every packet needs NT Kernel’s free Windows Packet Filter driver and the Sallyport service.':
    '按数据包分流程序需要 NT Kernel 免费的 Windows Packet Filter 驱动以及 Sallyport 服务。',
  'Defaults for new apps': '新应用的默认设置',
  'What an app you add starts with. Apps added before keep their own.': '新添加的应用采用的设置。之前添加的应用保持各自的设置。',
  'How to use': '使用方法',
  'About': '关于',
  'Import a profile.': '导入配置。',
  'Choose the OpenVPN profile supplied by your VPN provider (Profiles).': '选择 VPN 服务商提供的 OpenVPN 配置（“配置”页）。',
  'Add an app.': '添加应用。',
  'A game or program by its folder, routed by its packets; or a browser or proxy-aware program, launched with proxy settings (Apps).':
    '按文件夹添加游戏或程序，按数据包分流；或添加浏览器、支持代理的程序，启动时使用代理设置（“应用”页）。',
  'Run or launch it.': '分流或启动它。',
  'Sallyport connects the profile. Everything else on this computer keeps its own connection. Network shows what goes through it.':
    'Sallyport 会连接配置。此电脑上的其他一切保持使用自己的网络。“网络”页显示经由它的流量。',
  'Manual proxy setup': '手动设置代理',
  'Choose SOCKS5, host 127.0.0.1, the profile’s port, and remote DNS where available.': '选择 SOCKS5，主机 127.0.0.1，配置的端口，并在可用时启用远程 DNS。',
  'Programs routed by their packets go through the VPN from their first packet when routed before they start, TCP and UDP alike. Their IPv6 and their traffic to this computer’s own network are blocked, not routed. Programs launched with proxy settings carry TCP only.':
    '在程序启动前开始分流，按数据包分流的程序从第一个数据包起就经由 VPN，TCP 和 UDP 都是如此。它们的 IPv6 流量和发往本机局域网的流量会被阻止而不是分流。启动时使用代理设置的程序只承载 TCP。',
  'Closing this window keeps connections and routing running in the notification area. Choose ‘Exit Sallyport’ there to stop routing, disconnect and quit.':
    '关闭此窗口后，连接和分流会在通知区域继续运行。在那里选择“退出 Sallyport”即可停止分流、断开连接并退出。',
  // editors
  'Connection settings': '连接设置',
  'Program methods': '程序方式',
  'Edit app': '编辑应用',
  'Name': '名称',
  'VPN profile': 'VPN 配置',
  'Launch starts': '“启动”运行',
  'Nothing chosen': '未选择',
  'When the VPN drops (all-traffic programs)': 'VPN 断开时（全部流量方式的程序）',
  'When the VPN drops': 'VPN 断开时',
  'Use my own connection after': '在此时间后改用我自己的网络',
  'seconds': '秒',
  'never': '从不',
  'How its traffic reaches the VPN': '它的流量如何经由 VPN',
  'All traffic — packet driver': '全部流量 — 数据包驱动',
  'Everything it sends, TCP and UDP, while the app is routed.': '分流期间它发出的一切，包括 TCP 和 UDP。',
  'Proxy settings when launched here': '从这里启动时使用代理设置',
  'Started from Sallyport with the profile’s proxy. TCP only.': '从 Sallyport 启动并使用配置的代理。仅 TCP。',
  'How it is told about the proxy': '告知代理的方式',
  'Extra arguments when launched': '启动时的额外参数',
  'The first that can connect': '第一个可连接的配置',
  'Wait between attempts': '两次尝试间的等待',
  'Longest wait': '最长等待',
  'VPN protocol': 'VPN 协议',
  // Two sentences: Chinese runs them on after its full stop.
  '{0} {1}': '{0}{1}',
  'For every profile, from its next connection. A profile can be changed on its own under Profiles.': '适用于所有配置，从下次连接起生效。也可以在“配置”中单独更改某个配置。',
  'How it reaches the VPN server. Apps’ TCP and UDP go through the VPN either way.': '连接 VPN 服务器的方式。无论选哪种，应用的 TCP 和 UDP 都会经过 VPN。',
  'If it cannot connect': '无法连接时',
  'A round tries every server of the profile once, then waits a little longer each time.': '每一轮会把配置的每个服务器各尝试一次，之后每次等待稍长一些。',
  'Connection settings for {0}': '{0} 的连接设置',
  'Only for {0}, over the choices in Settings. Anything left as it is follows Settings and the profile.': '仅适用于 {0}，优先于“设置”中的选择。未更改的项目沿用“设置”和配置本身。',
  'Back to Profiles': '返回配置',
  'Back to Apps': '返回应用',
  'Open {0}': '打开 {0}',
  'Monitoring': '监控',
  'Not routed since Sallyport started. Its routing shows here while it is on.': '自 Sallyport 启动以来尚未分流。开启后，这里会显示它的分流情况。',
  'Through {0}: everything routed through it, this app’s and any other’s.': '经由 {0}：通过它的全部流量，包括此应用和其他应用的。',
  'Setup': '分流设置',
  'When it cannot connect': '无法连接时',
  'Keep trying until I disconnect': '持续重试，直到我断开',
  'Stop after a number of rounds': '尝试若干轮后停止',
  'A round tries every server once.': '每一轮会把每个服务器各尝试一次。',
  'Stop after': '停止前的轮数',
  'More settings': '更多设置',
  'rounds': '轮',
  'Time for a server to answer': '等待服务器应答的时间',
  'Lost after silence of': '静默多久视为断开',
  'Try the servers in random order': '以随机顺序尝试服务器',
  'Keep trying after the server refuses the login': '服务器拒绝登录后仍继续尝试',
};
