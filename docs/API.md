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
{ "name": "PrintBridge", "version": "1.2.0", "apiVersion": 1, "running": true, "printers": 2, "routeAllToDefault": false, "defaultPrinterAvailable": true }
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
| `requireReady` | Optional, `true`: ask the printer first and send nothing unless it is ready (`503 PRINTER_NOT_READY` with the reason). |
| `waitSeconds` | Optional, 0 to 30: wait this long for the printer to finish before answering, so `state` can be `printed`. 0 (default) answers as soon as the job is handed over. |

| Status | Body |
|---|---|
| 200 | `{ ok: true, printer, jobId, state, confirmed, message }`. `state`: `submitted` (handed over, nothing more known yet), `printing`, `printed`, `attention` (the printer reports a problem, see `message`). `confirmed` is true only when the printer or the system queue said the job is done. |
| 400 | `{ ok: false, error: "BAD_REQUEST", message }` (broken JSON, bad base64, unknown encoding, copies out of range, empty or oversized job) |
| 403 | plain text; the website is not on the allow-list (no CORS headers, so the browser reports a network error) |
| 404 | `{ ok: false, error: "PRINTER_NOT_FOUND" }` |
| 409 | `{ ok: false, error: "PRINT_CANCELLED" }` (Cancel in the preview, or the website stopped waiting) |
| 502 | `{ ok: false, error: "PRINT_FAILED", message }` (printer offline, paper out, network error; the message says how many copies went out first) |
| 503 | `{ ok: false, error: "DEFAULT_PRINTER_UNAVAILABLE" }` (the chosen default printer is unplugged; the job is not sent anywhere else) |
| 503 | `{ ok: false, error: "PRINTER_NOT_READY", message, printerState }` (only with `requireReady`) |

### `GET /api/v1/printer-status?printerId=<id or name>`
What the printer reports right now. Without `printerId` the default printer is used.
```json
{ "ok": true, "printer": "Zebra ZD421", "printerId": "tcp:192.168.1.52:9100", "ready": false,
  "state": "paper_out", "message": "Out of labels", "source": "printer", "issues": ["paper_out"], "busy": false }
```
`state`: `ready`, `printing`, `paused`, `paper_out`, `ribbon_out`, `head_open`, `paper_jam`, `offline`, `error`, `busy`, `unknown`.

`source` says how much to trust it:

| `source` | Meaning | Applies to |
|---|---|---|
| `printer` | The printer itself answered a status question. | Network printers set to ZPL (`~HS`) or TSPL (`ESC ! ?`) |
| `system` | State of the Windows or macOS print queue, as the driver reports it. Many label drivers report only offline and paused. | USB and driver printers |
| `network` | The printer accepts a connection. It cannot report paper or ribbon. | Other network printers |

### `GET /api/v1/jobs/<jobId>`
How far a job got: `{ ok, jobId, printer, state, confirmed, message, source, time }`. PrintBridge follows each job for about 20 seconds
after sending it. Kept in memory for the last 300 jobs; `404 JOB_NOT_FOUND` after a restart. What "printed" means depends on the printer:

| Printer | "printed" means |
|---|---|
| Network ZPL or TSPL | The printer reports no labels left to print and no error. |
| USB / driver printer | The job left the Windows or macOS print queue (delivered to the printer). The printer itself is not asked. |
| Other network printers | Never confirmed; the job stays `submitted`. |

Set `"FollowJobs": false` in `settings.json` to stop PrintBridge asking printers after each job.

Typical use:
```js
const r = await fetch('http://127.0.0.1:9100/api/v1/print', { method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ data: zpl, requireReady: true, waitSeconds: 10 }) });
const out = await r.json();
if (out.error === 'PRINTER_NOT_READY') alert('Printer: ' + out.message);          // e.g. "Out of labels"
else if (out.state === 'attention') alert('Label not finished: ' + out.message);
else if (!out.confirmed) console.log('Sent; this printer cannot confirm printing');
```

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

| `GET /config` | Application information, as `BrowserPrint.getApplicationConfiguration()` expects. Field names follow the Zebra application from memory; not checked against the real library. |
| `POST /convert` | `501`: PDF and picture conversion (`convertAndSendFile`) is not supported. |

`/read` also returns answers to Zebra settings questions written with `/write` (`! U1 getvar ...`, `{}{...}`), for network printers.

Device: `{ deviceType: "printer", uid, provider: "com.lifetrenz.printbridge", name, connection: "usb|network", version: 3, manufacturer }`.
