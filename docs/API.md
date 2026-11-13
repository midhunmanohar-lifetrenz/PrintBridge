# PrintBridge HTTP API

Base URLs: `http://127.0.0.1:9100` and `https://127.0.0.1:9101` (loopback only; any other `Host` gets `421`).
Safari must use HTTPS. Chrome and Edge also need the website's *Local network access* permission.

## Website gate (applies to every endpoint under `/api` and the Zebra endpoints)

1. The `Origin` header gives the website name. Only `http://` and `https://` origins are accepted (`403` for extensions, `file://`, app schemes and `null`).
   A request with no `Origin` and no browser marker is a program on the same computer (a script or desktop app). It is allowed while
   *Settings > Allow programs on this computer* is on (`allowLocalPrograms` in `policy.json`), is logged as "program on this computer",
   and goes through the print preview like a website. Turn the setting off on shared servers (Citrix, Remote Desktop).
2. The site must be in the allow-list (`AcceptedHosts`, wildcard `*.example.com` allowed). Unknown sites trigger an on-screen prompt (90 s timeout, then blocked). Blocked or unanswered: `403`.
3. CORS headers (including `Access-Control-Allow-Private-Network: true`) are sent only for allowed sites. Payload limit: 2 MB.

## Public pages (GET)

| Path | Purpose |
|---|---|
| `/` or `/test` | Test page |
| `/ssl_support` | Certificate status and help |
| `/ca.cer` | Download the per-install CA certificate |

## Native API

### `GET /api/v1/status`
```json
{ "name": "PrintBridge", "version": "1.1.0", "apiVersion": 1, "running": true, "printers": 2, "routeAllToDefault": false, "defaultPrinterAvailable": true }
```

### `GET /api/v1/printers`
List of visible printers: `id`, `name`, `vendor`, `language` (`zpl|tspl|epl|bple|cpcl|dpl|escpos|pdf|auto`), `transport` (`spooler|tcp`) and more.

### `POST /api/v1/print`
```json
{ "printerId": "<id or name>", "data": "^XA^FO50,50^ADN,36,20^FDHello^FS^XZ", "copies": 1 }
```
| Field | Meaning |
|---|---|
| `printerId` (or `printer`) | Printer id or name. Ignored while "send everything to the default printer" is on. |
| `data` | Label commands as text. |
| `encoding` | Optional. How `data` is turned into bytes: `utf-8` (default), `windows-1252`, `cp850`, `cp437`, any .NET code page name. Use it for EPL, DPL, ESC/POS and other languages that expect a code page. A per-printer default can be set with `printerEncodings` in `policy.json`. |
| `base64` | Use instead of `data` for binary payloads (graphics, ESC/POS). Sent byte for byte. |
| `copies` | 1 to 100. The whole job is sent that many times. Put larger quantities in the label itself (`^PQ`, `PRINT`, `P`). |

| Status | Body |
|---|---|
| 200 | `{ ok: true, printer, jobId, state, message }` |
| 400 | `{ ok: false, error: "BAD_REQUEST", message }` (broken JSON, bad base64, unknown encoding, copies out of range, empty or oversized job) |
| 403 | plain text; the website is not on the allow-list (no CORS headers, so the browser reports a network error) |
| 404 | `{ ok: false, error: "PRINTER_NOT_FOUND" }` |
| 409 | `{ ok: false, error: "PRINT_CANCELLED" }` (Cancel in the preview, or the website stopped waiting) |
| 502 | `{ ok: false, error: "PRINT_FAILED", message }` (printer offline, paper out, network error; the message says how many copies went out first) |
| 503 | `{ ok: false, error: "DEFAULT_PRINTER_UNAVAILABLE" }` (the chosen default printer is unplugged; the job is not sent anywhere else) |

When *Preview before printing* is on for the website, the request waits (up to 2 minutes) for the operator to confirm. The operator may change the number of copies.

Example:
```js
const r = await fetch('http://127.0.0.1:9100/api/v1/print', {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ printerId: 'Zebra ZD230', data: zpl, copies: 2 })
});
const out = await r.json();
```

## Browser permissions the web page must expect

- Chrome and Edge (142+) and Firefox (153+) ask the user once per website before a page may call `127.0.0.1`. Until the user allows it, `fetch` fails with a network error and PrintBridge never sees the request. Make the first call from a visible page (not a service worker) and show a hint when it fails.
- A page inside an iframe needs `allow="local-network; loopback-network; local-network-access"` on every enclosing iframe.
- IT can pre-approve sites with the browser policies `LoopbackNetworkAllowedForUrls` / `LocalNetworkAccessAllowedForUrls` (Chrome, Edge) and `LocalNetworkAccess.SkipDomains` (Firefox). Confirm the names against the current browser documentation.
- Safari only works over `https://127.0.0.1:9101` with the PrintBridge certificate trusted.

## Zebra Browser Print compatibility (always on)

| Call | Purpose |
|---|---|
| `GET /available` | `{ printer: [device...] }` |
| `GET /default` | Default device, or empty `200` |
| `POST /write` | `{ device: { uid | name }, data }` or `{ device, base64 }` for binary data. Errors are plain text: 400, 404, 409, 500, 503. |
| `POST /read` | `{ device: { uid } }` returns the printer's answer to the last status question written to it (`~HS`, `~HI`, `~HQ`, `^HH`, TSPL `<ESC>!?`). Network printers only; empty for USB and driver printers. |

Device: `{ deviceType: "printer", uid, provider: "com.lifetrenz.printbridge", name, connection: "usb|network", version: 3, manufacturer }`.
