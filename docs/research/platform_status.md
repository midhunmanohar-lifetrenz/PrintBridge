# Platform status for a localhost print bridge (127.0.0.1:9100 HTTP / :9101 HTTPS)

Researched 12 Nov 2026. Tags: **[confirmed]** = read on a primary source (vendor doc, bug tracker, spec list) during this research; **[likely]** = secondary source(s) or primary source that is indirect/older; **[unconfirmed]** = could not verify, conflicting sources, or from background knowledge only.

Where a page carries a date, it is given. Sources that conflict are called out explicitly.

---

## 1. Chrome / Edge Local Network Access (LNA)

### Timeline
- LNA permission prompt launched in **Chrome 142** (stable 28 Oct 2025) for fetch/subresources/subframe navigations; opt-in testing from Chrome 138. [confirmed] https://developer.chrome.com/blog/local-network-access (published 9 Jun 2025, update 29 Sep 2025)
- LNA replaces Private Network Access (PNA), which was "put on hold". [confirmed] same URL
- **Chrome 145**: the single `local-network-access` permission was split into `local-network` (RFC1918, link-local, ULA, `.local`) and `loopback-network` (127.0.0.0/8, ::1, `localhost`). Old name kept as an alias for `permissions.query` and Permissions Policy "for the foreseeable future". Intent to Ship posted and approved 9 Jan 2026. [confirmed] https://groups.google.com/a/chromium.org/g/blink-dev/c/Zl3P6NHhMBw
- **Chrome 147** (rollout from ~7 Apr 2026): LNA extended to **WebSocket** connections (Intent to Ship 20 Feb 2026, milestone 147 desktop + Android). [confirmed] https://groups.google.com/a/chromium.org/g/blink-dev/c/O6GMKt44Ups ; date of rollout per QZ [likely] https://qz.io/docs/lna
- **WebTransport** also gated from 147 (Edge 147 release notes say both WebSocket and WebTransport now prompt). [likely — search summary of the Edge 147 notes, page not opened] https://learn.microsoft.com/en-us/Microsoft-Edge/web-platform/release-notes/147
- WebRTC coverage: one vendor note (Jamf) says Chrome 146 extended enforcement to WebRTC. [unconfirmed] https://support.jamf.com/en/articles/12894739-chrome-local-network-access-prompts-with-jamf-trust-ztna
- Chrome 154: Background Fetch to local/loopback now needs LNA permission; a WebSocket `targetAddressSpace` option was "pending review". [confirmed] https://chromestatus.com/release-notes/154
- **Edge**: LNA restrictions on by default from **Edge 143**; programmatic prompt trigger (`fetch("http://localhost")`) works from Edge 144; WebSocket/WebTransport/WebRTC were initially excluded. [confirmed] https://learn.microsoft.com/en-us/deployedge/ms-edge-local-network-access (text "Last updated March 9, 2026", page metadata updated 6 Oct 2026)

### Loopback specifics
- A public-origin page fetching 127.0.0.1/localhost needs the **`loopback-network`** permission (prompt text is the "apps on this device" style prompt). [confirmed] https://developer.chrome.com/docs/modern-web-guidance/guides/security/local-network-access
- The permission is only requestable from a **secure context**; from an HTTP top-level page it is auto-denied. The HTTP-origin origin trial ended with Edge 146 / (per QZ) September 2026 for Chrome. [confirmed for Edge] https://learn.microsoft.com/en-us/deployedge/ms-edge-local-network-access ; [likely for Chrome] https://qz.io/docs/lna
- **Mixed content: `http://127.0.0.1` / `http://localhost` from an HTTPS page is still allowed** — Microsoft: "localhost is considered a secure origin by the mixed content specification and won't be blocked as mixed content". [confirmed] https://learn.microsoft.com/en-us/deployedge/ms-edge-local-network-access
- For `ws://` from an HTTPS page, Chrome exempts private/loopback IP literals, `*.localhost` and `.local`; the `WebSocket` constructor has no options object. For a public hostname that resolves to loopback use `fetch(url, {targetAddressSpace: 'loopback'})`. [confirmed] https://developer.chrome.com/docs/modern-web-guidance/guides/security/local-network-access
- Ignoring the prompt three times is treated as "block". [likely — QZ doc] https://qz.io/docs/lna
- Service Workers / Shared Workers cannot trigger the prompt; they work only if the origin already holds the permission. Dedicated workers prompt in the owning window. [confirmed] https://learn.microsoft.com/en-us/deployedge/ms-edge-local-network-access
- LNA does not apply to extensions with host permissions, to main-frame navigation, or (yet) to local->local / local->loopback requests. [confirmed] same URL

### Iframes
- Default allowlist is `self`; cross-origin iframes must be delegated. Current syntax: `allow="local-network; loopback-network"` (Chrome guide) — legacy `allow="local-network-access"` still works as alias (Edge doc still shows the legacy token). Every nested frame level must delegate; permission is attributed to the **top-level (embedding) origin**. Delegation is required even when an enterprise allow policy is set (unless `LocalNetworkAccessPermissionsPolicyDefaultEnabled` is on). [confirmed] https://developer.chrome.com/docs/modern-web-guidance/guides/security/local-network-access and https://learn.microsoft.com/en-us/deployedge/ms-edge-local-network-access
- Header form: `Permissions-Policy: loopback-network=(self "https://embedded.example")`. [confirmed] Chrome guide URL above

### Permissions API
- Query `navigator.permissions.query({name: 'loopback-network'})`; Chrome guide says do not query the legacy `local-network-access` name (can crash older renderers); on `TypeError` treat as `'prompt'`. Always returns `denied` on an HTTP page. [confirmed] Chrome guide URL above; Edge doc URL above

### Enterprise policies (real names)
- `LocalNetworkAccessAllowedForUrls`, `LocalNetworkAccessBlockedForUrls` — Chrome 139+ / Edge. Apply to the top-level origin; URL-pattern syntax; cover both address spaces. [confirmed for Edge] https://learn.microsoft.com/en-us/deployedge/ms-edge-local-network-access ; [likely for Chrome version] https://developer.samsung.com/browser/policy/local-network-access-allowed-for-urls.html
- `LoopbackNetworkAllowedForUrls`, `LoopbackNetworkBlockedForUrls` (loopback only) and `LocalNetworkAllowedForUrls`, `LocalNetworkBlockedForUrls` (LAN only) — Edge >= 146 (Windows/macOS/Android). Precedence for loopback: LoopbackNetworkBlockedForUrls > LoopbackNetworkAllowedForUrls > LocalNetworkAccessBlockedForUrls > LocalNetworkAccessAllowedForUrls. Registry `SOFTWARE\Policies\Microsoft\Edge\LoopbackNetworkAllowedForUrls\1..n`. [confirmed] https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/loopbacknetworkallowedforurls (ms.date 20 May 2026, updated 3 Nov 2026) and https://learn.microsoft.com/en-ca/DeployEdge/microsoft-edge-policies/localnetworkblockedforurls (11 Sep 2026)
- Same names exist in Chrome (Chrome 154 release notes list `LoopbackNetworkAllowedForUrls`, `LocalNetworkAccessAllowedForUrls`, `LocalNetworkAccessPermissionsPolicyDefaultEnabled`, `LocalNetworkAccessIpAddressSpaceOverrides`, `LocalNetworkAccessRestrictionsTemporaryOptOut`). Chrome registry root is `SOFTWARE\Policies\Google\Chrome`. [confirmed names] https://chromestatus.com/release-notes/154 ; [likely path] https://docs.truu.ai/docs/mandatory-gpo-update-for-chrome-version-147
- `LocalNetworkAccessIpAddressSpaceOverrides`, `LocalNetworkAccessPermissionsPolicyDefaultEnabled` — from 146. [confirmed] Edge doc URL above
- `LocalNetworkAccessRestrictionsEnabled` — pre-launch toggle (138–144), deprecated from Edge 144 because LNA is default-on. [likely] https://learn.microsoft.com/lb-lu/DeployEdge/microsoft-edge-browser-policies/localnetworkaccessrestrictionsenabled
- `LocalNetworkAccessRestrictionsTemporaryOptOut` — global opt-out. **Removal milestone is contradictory**: Edge policy page (11 Sep 2026) says "removed after Microsoft Edge version 163"; Edge guide text says "after Edge 152"; a third-party article (25 Sep 2026) claims chromestatus says Chrome 156 (stable 20 Oct 2026); Jamf says 146. Chrome 154 release notes still list it as usable. Treat as going away; do not depend on it. [unconfirmed] https://learn.microsoft.com/en-us/deployedge/microsoft-edge-browser-policies/localnetworkaccessrestrictionstemporaryoptout , https://www.superchargebrowser.com/library/chrome-156-local-network-access.md
- I could not open chromeenterprise.google policy pages or Chrome 155/156 release notes directly. [unconfirmed]

### PNA preflight header
- `Access-Control-Request-Private-Network` / `Access-Control-Allow-Private-Network` preflights were never enforced as blocking and should not be added to new code; servers that already send `Access-Control-Allow-Private-Network: true` can keep it harmlessly. Normal CORS still applies. [confirmed] https://developer.chrome.com/docs/modern-web-guidance/guides/security/local-network-access

---

## 2. Firefox

- **Firefox 153 (21 Jul 2026)**: "Local Network Access restrictions are now enabled by default for all users" — sites must ask before reaching LAN devices **or apps and services on your device** (loopback is covered). [confirmed] https://www.firefox.com/firefox/153.0/releasenotes/
- Earlier: on by default for ETP-Strict users from Firefox 147 (Jan 2026). [likely] https://datawookie.dev/blog/2026/01/whats-new-in-firefox-147
- **Firefox 154 (Aug 2026)**: extended to WebSocket connections. [likely] https://cyberinsider.com/firefox-154-blocks-silent-websocket-access-to-local-network-devices/ (19 Aug 2026)
- Prompt has a "remember this decision" checkbox. [likely] https://qz.io/docs/lna
- Enterprise policy **`LocalNetworkAccess`** with keys `Enabled`, `BlockTrackers`, `EnablePrompting`, `SkipDomains` (array; matches requesting site or target; `*.` prefix), `Locked`. Supported Firefox 145+, ESR 153+. Prefs: `network.lna.enabled`, `network.lna.blocking`, `network.lna.block_trackers`, `network.lna.skip-domains`. [confirmed] https://firefox-admin-docs.mozilla.org/reference/policies/localnetworkaccess/
- User-facing help page (could not be rendered by my fetcher). [unconfirmed content] https://support.mozilla.org/en-US/kb/control-personal-device-local-network-permissions-firefox
- Mixed content: Firefox treats `http://127.0.0.1` (since Firefox 55) and `http://localhost` (since ~84) as potentially trustworthy, so HTTPS pages may fetch them. [likely] https://bugzilla.mozilla.org/show_bug.cgi?id=903966 , https://bugzilla.mozilla.org/show_bug.cgi?id=1220810
- **OS root store**: since Firefox 120 Firefox by default uses third-party/user-added TLS trust anchors from the OS store on Windows, macOS and Android (`security.enterprise_roots.enabled` = true; UI checkbox "Allow Firefox to automatically trust third-party root certificates you install"). [confirmed via bug] https://bugzilla.mozilla.org/show_bug.cgi?id=1848815 ; [likely] https://support.mozilla.org/kb/setting-certificate-authorities-firefox
  - Nuance: imports user/admin-added roots only. Users can turn it off; Linux is not covered. [likely] https://joshua.hu/mozilla-firefox-trusting-system-root-stores-qwacs-eu
- 0.0.0.0 blocking status in Firefox not re-verified. [unconfirmed]

---

## 3. Safari / WebKit and macOS Local Network privacy

- **WebKit bug 171934** ("Content from loopback addresses should not be considered mixed content") is still **NEW / unresolved**, last modified 19 Aug 2026. Comments from Mar 2026 report Safari still blocks `http://127.0.0.1` and `ws://` loopback from HTTPS pages; WebKit engineers (Jun 2025) said they object to opening localhost and prefer alternatives to prompts. [confirmed] https://bugs.webkit.org/show_bug.cgi?id=171934
  - Consequence: Safari needs the HTTPS listener (9101) with a certificate chaining to a root trusted in the macOS keychain. Safari uses the system trust store. [likely — background knowledge]
- WebKit LNA implementation: meta bug 250607 "Implement Local Network Access" is NEW (modified 21 Jul 2026); "Enable Local Network Access by default" (295047) NEW; the check algorithm landed in trunk 11 Sep 2026 (bug 319907) with **no native permission prompt yet** (prompt treated as denied, test-only stub). Not shipped in any Safari release I could identify. [confirmed] https://bugs.webkit.org/show_bug.cgi?id=250607 , https://www2.webkit.org/show_bug.cgi?id=319907
- Safari 26.2 reportedly throws `TypeError` when `targetAddressSpace: 'loopback'` is passed to fetch. [unconfirmed — search snippet only] https://bugs.webkit.org/show_bug.cgi?id=304164
- No Safari website-level local-network prompt in Safari 18/26. [likely] https://developer.apple.com/forums/thread/811690 , https://qz.io/docs/lna

### macOS "Local Network" privacy (TN3179, revised 6 Oct 2026)
All [confirmed] from https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy unless noted.
- Introduced macOS 15; per-user state; cannot be configured by MDM.
- A "local network" is an IP network on a **broadcast-capable interface** (Wi-Fi/Ethernet). **Loopback is not one, so 127.0.0.1 traffic is unaffected** [likely — follows from the definition; TN3179 does not name loopback explicitly].
- **Listening for / accepting incoming TCP: not required.** So the bridge's own listener never prompts.
- **Outgoing TCP to a LAN address (e.g. printer 192.168.x.x:9100): YES, requires Local Network permission.** Also Bonjour browse/resolve and `.local` resolution.
- Exempt automatically: launchd **daemons** (not agents), anything running as root, CLI tools run from Terminal/SSH, and traffic from Safari/WKWebView.
- launchd agents not installed via `SMAppService` should set `AssociatedBundleIdentifiers` so the prompt is attributed to the app.
- Add `NSLocalNetworkUsageDescription` (and `NSBonjourServices` if browsing) to Info.plist.
- **Unsigned / ad-hoc signed code**: identity is tracked by code signature and main-executable UUID; Apple says to sign with an Apple-issued identity "to ensure that local network privacy reliably tracks the identity". Ad-hoc builds may re-prompt or lose the grant on every rebuild/update. [confirmed]; "may not be reliable with ad hoc" [likely] https://eclecticlight.co/2026/10/22/local-network-privacy-is-getting-better/
- Operations may be denied before the user answers the alert — retry / use `NWConnection` (`.waiting` + `unsatisfiedReason == .localNetworkDenied`).
- Short-lived processes may never show the alert (FB16131937).
- Admin escape hatch (macOS 15.5+): `sudo defaults write com.apple.network.local-network AllowedEthernetLocalNetworkAddresses/AllowedWiFiLocalNetworkAddresses -array "<CIDR>"` + restart.
- **macOS 27.2** added +/- buttons to System Settings > Privacy & Security > Local Network; before that there was no way to reset an app's state.
- Chrome/Edge/Firefox themselves need this macOS permission to reach LAN devices, but not for loopback. [likely] https://developer.apple.com/forums/thread/811690

---

## 4. Windows

### Printing
Microsoft's end-of-servicing plan (ms.date 9 May 2025, still the current page): [confirmed] https://learn.microsoft.com/en-us/windows-hardware/drivers/print/end-of-servicing-plan-for-third-party-printer-drivers-on-windows
- 15 Jan 2026 — Windows 11+/Server 2025+: no new printer drivers published to Windows Update; updates case-by-case.
- 1 Jul 2026 — driver ranking changed to always prefer the Windows IPP inbox class driver.
- 1 Jul 2027 — third-party driver updates no longer allowed except security fixes. Existing drivers still install from Windows Update or vendor packages.
- FAQ: Windows will continue to allow vendor installer packages; "no plans to disable print features specifically related to the legacy printer driver platform".
- **Windows Protected Print Mode (WPP)** remains opt-in; when enabled it blocks third-party (v3/v4) drivers and only Mopria/IPP printers work. Vendors widely cite "default by 2027" but I found no Microsoft commitment to a date. [likely / date unconfirmed] https://www.ezeep.com/learn/what-is-wpp , https://www.papercut.com/blog/print_tips/wpp-vs-windows-ready-print-what-changed-and-what-it-teams-need-to-know/ (18 Sep 2026)
- Microsoft renamed the Modern Print Platform to "Windows Ready Print" (Jun 2026); it is a preference, not a lockout. [likely] same PaperCut URL
- ZPL/EPL are not part of what the IPP class driver speaks; label printers that rely on a vendor driver + RAW datatype are at risk on WPP machines. Direct TCP 9100 from the bridge does not depend on the spooler. [likely] https://www.ezeep.com/blog/label-printers-windows-protected-print-mode ; RAW-datatype behaviour under WPP [unconfirmed]

### Smart App Control (SAC)
- Unsigned files run only if Microsoft's cloud (Intelligent Security Graph) knows them as trustworthy; signed files must chain to a root in Microsoft's Trusted Root Program — **locally installed roots do not count**. SAC evaluates all loaded code (DLLs, MSIs, scripts), not just the entry EXE; sign everything with SHA-256, RSA preferred. When SAC is on, SmartScreen AppRep is disabled. [likely — Eric Lawrence blog, 28 Apr 2026] https://textslashplain.com/2026/04/28/smart-app-control/
- SAC can now be turned on/off without reinstalling (Windows 11 25H2, Windows Security app v1000.29554+); starts in Evaluation mode and may enable itself. [likely] same URL ; https://support.microsoft.com/kb/5008908
- No per-app exception exists. [likely] https://learn.microsoft.com/en-us/answers/questions/5823237/smart-app-control
- Reports of an Oct 2026 update (KB5127648) breaking the SAC toggle. [unconfirmed] https://www.it-connect.tech/windows-11-kb5127648-breaks-smart-app-control-in-october-2026-update/

### Azure Artifact Signing (formerly Trusted Signing)
- Public Trust: **organizations** in US, Canada, EU, UK, Australia, New Zealand, Japan, South Korea, Singapore, Switzerland, Norway, Israel. **Individuals: US or Canada only.** **India is not listed** for either. Restrictions do not apply to Private Trust. [confirmed] https://learn.microsoft.com/azure/artifact-signing/quickstart (ms.date 21 May 2026, updated 8 Oct 2026)
- No published timeline for more countries. [likely] https://learn.microsoft.com/en-us/answers/questions/2243504/trusted-signing-for-other-countries
- Artifact Signing does not guarantee instant SmartScreen reputation; forum reports of an Apr 2026 regression after new intermediate CAs. [unconfirmed] https://learn.microsoft.com/en-us/answers/questions/5855708/trusted-signing-regression-in-smartscreen-reputati
- Alternative for India: OV/EV code-signing certificate from a commercial CA (key on HSM/token). [likely — background knowledge]

### Code-signing certificate lifetime
- CA/B Forum ballot CSC-31 (adopted 17 Nov 2025, CSBR v3.10.0): max validity cut from 39 months to **460 days for certificates issued from 1 Mar 2026**. [confirmed] https://cabforum.org/2025/11/17/ballot-csc-31-maximum-validity-reduction
- Some CAs issue at 458 days. [likely] https://www.ssl.com/article/reminder-reduced-code-signing-certificate-lifecycles/amp/
- Always timestamp signatures so binaries stay valid after the certificate expires. [likely — background knowledge]

---

## 5. Apple

- **macOS naming**: macOS 26 = Tahoe; macOS 27 = "Golden Gate" (announced WWDC 8 Jun 2026); 27.2 was current/expected late Oct 2026. [likely — secondary sites + Howard Oakley] https://eclecticlight.co/2026/10/22/local-network-privacy-is-getting-better/ , https://lowendmac.com/2026/macos-27-0-golden-gate/
- macOS 27 runs on Apple silicon only; it is the last release with general-purpose Rosetta 2. [likely] https://www.techrepublic.com/article/news-apple-macos-27-drops-intel-mac-support-rosetta/ . One third-party claim that macOS 27 lacks Rosetta entirely conflicts with this. [unconfirmed]
- **Developer ID certificates**: now issued from a G2 sub-CA and valid **one year** (was five). From **1 Feb 2027** Apple warns ".pkg files signed with an affected certificate will no longer install"; re-sign packages with the new Installer certificate before then. Apps signed with a secure timestamp while the cert was valid keep working. [likely — Eclectic Light 16 Oct 2026, citing Apple's "Certificates overview"] https://eclecticlight.co/2026/10/16/changing-developer-certificates-might-affect-installers-next-year/ ; https://developer.apple.com/forums/thread/854453
- **Notarization**: I found no new 2026 notarization requirement; `notarytool` remains the tool (altool removed Nov 2023); rules for running un-notarized code reported unchanged. [unconfirmed — absence of evidence; Apple developer news not opened]
- **CUPS raw queues**: deprecated since CUPS 2.2 (2018), drivers/PPDs since 2.3; `lpadmin -m raw` prints "Raw queues are deprecated and will stop working in a future version of CUPS"; still functioning in CUPS 2.x logs dated Jun 2026. [likely] https://openprinting.github.io/cups/drivers.html , https://man.archlinux.org/man/extra/cups/lpadmin.8.en
- Whether macOS 26/27 changed raw-queue behaviour, and whether `lp -o raw` on a PPD queue still bypasses filters there: **not verified**. On CUPS 2.x `-o raw` on a PPD queue submits as raw and skips filters. [unconfirmed for macOS 26/27; likely for CUPS 2.x from background knowledge]
- Direction is driverless (IPP Everywhere / AirPrint) plus Printer Applications; OpenPrinting's own example is a label app generating ZPL and spooling it raw with `lp`. [likely] https://openprinting.github.io/cups/drivers.html
- "Printing via UIKit"/AirPlay style high-level services are exempt from Local Network privacy; raw sockets are not. [confirmed] TN3179 URL above

---

## 6. Zebra Browser Print, QZ Tray, web standards

- **Zebra Browser Print**: Zebra's product page lists no version, release notes, ports, or LNA guidance; downloads are now request-form gated; Zebra support requires MFA from 1 Jul 2026. [confirmed that the page lacks this] https://www.zebra.com/us/en/support-downloads/software/printer-software/browser-print.html
  - Current version number: **could not verify** (last I know of is 1.3.2). [unconfirmed]
  - Ports 9100 (HTTP) / 9101 (HTTPS) on 127.0.0.1 and the `/available`, `/default`, `/write`, `/read` API: from background knowledge, no change found. [unconfirmed]
  - No Zebra statement on Chrome LNA found. Third parties report Zebra printing via Browser Print broke/prompted from Chrome 142 and tell users to set the site permission to Allow or deploy `LocalNetworkAccessAllowedForUrls`. [likely] https://help.extensiv.com/troubleshooting-guides/workaround-for-connecting-to-zebra-printers-after-chrome-142-update , https://knowledgebase.scinote.net/en/knowledge/new-local-network-access-lna-permission-prompt
- **QZ Tray**: latest **2.3.0 (11 Sep 2026)** — adds LNA detection examples, Basic Constraints on its CA cert; 2.2.6 (4 Apr 2026). [confirmed] https://github.com/qzind/tray/releases
  - LNA handling: no agent-side fix is possible; they ship `qz-lna.js` (npm `qz-lna`) for clearer error messaging, tell integrators to show an explainer when the connection fails, to use `allow="loopback-network ..."` on iframes, and to deploy `LocalNetworkAccessAllowedForUrls`; branded builds can provision policy. They state prompt-state detection is unreliable. [confirmed] https://qz.io/docs/lna
- **JSPrintManager** (Neodynamic) publishes equivalent LNA guidance. [likely] https://www.neodynamic.com/articles/JSPrintManager-LNA
- **Web Printing API**: Intent to Ship 11–12 Feb 2026, est. Chrome 147 desktop, **Isolated Web Apps only**, IPP-attribute based (not a raw/ZPL channel). [confirmed] https://groups.google.com/a/chromium.org/g/blink-dev/c/6MmqQj3mzcY
- **IWA / Direct Sockets**: `direct-sockets-private` policy merged into `local-network` / `loopback-network` (auto-unpacked until Chrome 151). IWAs with Direct Sockets could open TCP 9100 directly but require IWA packaging/enterprise install. [likely] https://groups.google.com/a/chromium.org/g/iwa-dev/c/-J49irbq8Wc
- **WebUSB / WebSerial**: Chromium-only, not in Firefox or Safari; on Windows WebUSB cannot claim a printer bound to the usbprint driver without swapping to WinUSB. No 2026 change found. [unconfirmed — background knowledge, not searched in depth]

---

## 7. Security practice for localhost bridges

- GitHub Security Lab, "Localhost dangers: CORS and DNS rebinding" (3 Apr 2025): require authentication on sensitive endpoints; validate the **Host** header against an allowlist; verify **Origin** against an exact-match allowlist; never wildcard or reflect origins; never allow the `null` origin; avoid `startsWith`/`contains` matching. [confirmed] https://github.blog/security/application-security/localhost-dangers-cors-and-dns-rebinding/
- NCC Group Singularity wiki: Host-header allowlisting is the core DNS-rebinding defence; binding to loopback, Origin checks and CSRF tokens alone do not stop same-origin rebinding. [likely] https://github.com/nccgroup/singularity/wiki/Preventing-DNS-Rebinding-Attacks
- Oligo "0.0.0.0 Day" (Aug 2024): pages could reach localhost services through `0.0.0.0` on macOS/Linux, bypassing PNA; Chrome blocks 0.0.0.0 (Chromium 128 -> full by 133), Safari 18 blocks it; Firefox status unverified. [likely] https://www.oligo.security/blog/0-0-0-0-day-exploiting-localhost-apis-from-the-browser , https://www.helpnetsecurity.com/2024/08/09/0-0-0-0-day-vulnerability-affects-chrome-safari-and-firefox/
- Recent real-world instance: CVE-2025-9611, Playwright MCP server < 0.0.40 — DNS rebinding via missing Origin/Host validation; fix returns 403 on mismatched Host. [likely] https://advisories.gitlab.com/pkg/npm/@playwright/mcp/
- Derived checklist for this app (my synthesis, not from one source):
  1. Bind only to 127.0.0.1 (and ::1 if needed), never 0.0.0.0.
  2. Reject any request whose `Host` is not exactly `127.0.0.1:9100/9101`, `localhost:9100/9101` (or `[::1]`), with 403.
  3. Enforce the per-Origin allow/block decision **server-side on every request** (including `/write`), because simple `text/plain` POSTs (the Zebra API shape) are sent without a CORS preflight and CORS only hides the response. Treat missing or `null` Origin as untrusted for state-changing endpoints.
  4. Echo the specific allowed origin in `Access-Control-Allow-Origin` with `Vary: Origin`; no `*`, no reflection of unapproved origins.
  5. Per-install CA: constrain it (name constraints / leaf only for 127.0.0.1 + localhost), discard or protect the CA private key after issuing the leaf, set Basic Constraints correctly (QZ fixed this in 2.3.0).
  6. Rate-limit / visibly log print jobs per origin; the browser LNA permission is per top-level site and does not replace the app's own origin prompt.

---

## Not verified / gaps
- Chrome 155/156/157 release notes and chromeenterprise.google policy pages (opt-out removal milestone; official Chrome "supported since" versions for the Loopback*/LocalNetwork* policies).
- Mozilla's SUMO pages (fetcher got a JS error page).
- Zebra Browser Print current version, release notes, and any official LNA statement.
- Behaviour of `lp -o raw` / raw queues on macOS 26 and 27 specifically; any macOS 27 CUPS change.
- Any 2026 Apple notarization policy change (none found, Apple news not opened).
- Microsoft date for WPP becoming default; RAW datatype behaviour under WPP.
- Safari 26.x actual behaviour with `https://127.0.0.1:9101` + user-trusted CA (inferred, not tested), and the 304164 `targetAddressSpace` TypeError report.
- Whether Firefox now blocks 0.0.0.0.
