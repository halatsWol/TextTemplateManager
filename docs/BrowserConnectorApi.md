# Browser Connector API

Text Template Manager exposes a small local HTTP API that a browser extension (Chrome / Edge /
Firefox) can call to list templates, fetch rendered content, and create new templates from selected
text. It is meant for building a context-menu "insert template" extension.

## Enabling & pairing

- The user enables it in **Settings ▸ General ▸ Browser extensions** (off by default).
- On first enable, the app generates a random **token** and shows it in settings. The user copies
  it into the extension. Every request must send it as the `x-ttm-token` header.
- The server binds **loopback only** (`127.0.0.1`), default port **47615** (configurable in settings).
- CORS is granted only to `chrome-extension://`, `moz-extension://`, and `safari-web-extension://`
  origins. A web page cannot use the API (wrong origin, and it can't know the token).
  Microsoft Edge is Chromium-based and uses the `chrome-extension://` scheme, so Edge extensions are
  covered by the same origin (there is no separate `edge-extension://`).

The app must be running for the API to answer. If it isn't (or the connector is disabled), requests
fail to connect.

## Base URL

```
http://127.0.0.1:<port>/
```

All responses are `application/json; charset=utf-8`.

## Auth & headers

| Header | Required | Value |
| --- | --- | --- |
| `x-ttm-token` | yes | the token from the app's settings |
| `Origin` | yes (browsers send it) | must be an extension origin |

Errors: `401` (missing/wrong token), `403` (non-extension origin), `400` (bad request),
`404` (unknown endpoint or template id), `500` (internal).

## Endpoints

### `GET /ping`
Connectivity + version check.
```json
{ "app": "TextTemplateManager", "version": "1.0.1", "protocol": 2 }
```

### `GET /pastemodes`
The paste modes, in display order. `id` is what you pass to `/template?mode=`.
```json
[ { "id": "Auto", "label": "Auto" },
  { "id": "Jira", "label": "HTML/Jira" },
  { "id": "HTML", "label": "HTML" },
  { "id": "RTF", "label": "RTF" },
  { "id": "Markdown", "label": "Markdown" },
  { "id": "Plaintext", "label": "Plaintext" } ]
```

### `GET /tree`
The folder/template tree (names + ids only — no content). Build your menu from this.
```json
[ { "id": "<guid>", "name": "Greetings", "type": "folder", "source": "local",
    "children": [
      { "id": "<guid>", "name": "Kind Regards", "type": "template",
        "defaultMode": "Auto", "source": "local" } ] } ]
```
- `type` is `"folder"` or `"template"`.
- `defaultMode` (templates only) is the template's own default paste mode.
- `source` is the sync folder's name, or `"local"`.

### `GET /template?id=<guid>&mode=<mode>`
Rendered content for one template. `mode` is a `/pastemodes` id, or `default` / omitted to use the
template's own default.
```json
{ "id": "<guid>", "name": "Kind Regards", "mode": "HTML",
  "contentType": "text/html", "content": "<p>Kind regards</p>" }
```
`contentType` tells you how to use `content`:

| mode | contentType | how to insert |
| --- | --- | --- |
| Auto / HTML / Jira | `text/html` | insert as HTML (e.g. into a `contenteditable`, or write `text/html` to the clipboard) |
| Markdown | `text/markdown` | insert the markdown source as text |
| Plaintext | `text/plain` | insert as plain text |
| RTF | `application/rtf` | RTF source (mainly useful for clipboard) |

### `POST /template`
Create a new template in the **local** area (e.g. from the page selection). Send a JSON body with
`Content-Type: application/json`:
```json
{ "content": "<p>selected text</p>", "name": "optional title" }
```
- `content` (**required**) — the template body. Templates hold HTML (the same format the in-app editor
  saves), so send HTML; plain text is stored verbatim. The content is **sanitized** before it is stored
  (see [Content sanitizing](#content-sanitizing)), so active/script markup is stripped — send the
  formatting you want kept and don't rely on anything scriptable surviving.
- `name` (optional) — the title. Defaults to `New Template`. A name that already exists gets an
  incrementing suffix (`New Template 2`, …), so the returned title may differ from what you sent.

Returns the created template's id and final name:
```json
{ "id": "<guid>", "name": "New Template 2" }
```
`400` if `content` is missing or empty. The template appears in the app immediately and in the next
`/tree`. (Available from `protocol` 2.)

## Example (extension background)

```js
const BASE = "http://127.0.0.1:47615";
const TOKEN = "<paired token>";
const h = { "x-ttm-token": TOKEN };

const tree  = await (await fetch(`${BASE}/tree`,  { headers: h })).json();
const tpl   = await (await fetch(`${BASE}/template?id=${id}&mode=default`, { headers: h })).json();
// tpl.content + tpl.contentType -> insert into the page

// Create a template from the current selection:
const created = await (await fetch(`${BASE}/template`, {
  method: "POST",
  headers: { ...h, "Content-Type": "application/json" },
  body: JSON.stringify({ content: selectedHtml, name: "From browser" }),
})).json();
// created.id, created.name
```

`manifest.json` needs host permission for the loopback origin:
```json
"host_permissions": ["http://127.0.0.1:47615/*"]
```
(Loopback is a secure context, so this works from an HTTPS page's extension without mixed-content
issues. Match the port to the app setting.)

## Content sanitizing

Template HTML is untrusted (a page selection can carry attacker-controlled markup), so it is sanitized
**both directions** — when a template is created via `POST /template` and when content is served by
`GET /template`. Sanitizing is a two-stage, DOM-based process: a strict **allow-list**, then a
**deny-list** hardening pass that also re-parses the result to catch anything a serialization round-trip
could resurrect (mutation-XSS). Text nodes are never altered, so **code blocks pass through verbatim** —
code in any language, including HTML/JS shown as escaped text inside `<pre>`/`<code>`, is preserved
(only a *real* `<script>` element is dropped, not the string `<script>` shown as code).

**Kept**

- Block/inline formatting: `p`, `h1`–`h6`, `strong`/`b`, `em`/`i`, `u`, `s`, `del`, `ins`, `mark`,
  `sub`, `sup`, `small`, `code`, `pre`, `blockquote`, `br`, `hr`, `ul`/`ol`/`li`, and tables
  (`table`/`thead`/`tbody`/`tr`/`td`/`th`/`caption`/`colgroup`/`col`).
- Links: `<a>` with `href` limited to `http`, `https`, `mailto`, `tel`, or relative/anchor URLs. A link
  with a `target` gets `rel="noopener noreferrer"` added.
- Inline `style` limited to inert properties (`color`, `background-color`, `text-align`, `width`,
  `height`, `font-weight`, `font-style`, `text-decoration`, `vertical-align`).
- Inert `class` and `data-*` attributes (so code-block language classes and panel markers round-trip).

**Removed**

- Active / remote-loading / legacy elements — removed **with their contents**: `script`, `style`,
  `iframe`, `frame`, `object`, `embed`, `applet`, `form` (and inputs), `svg`, `math`, `img`/`picture`,
  `video`/`audio`/`source`/`track`, `link`, `meta`, `base`, `noscript`, `template`, and parser-mode
  legacy tags (`xmp`, `plaintext`, `listing`, `marquee`, …). **Images are not kept.**
- HTML comments (a conditional-comment mutation-XSS vector).
- Event-handler attributes (`onclick`, `onerror`, `onload`, … — anything starting with `on`).
- Dangerous URL schemes anywhere a URL is expected: `javascript:`, `vbscript:`, `data:` and similar,
  including entity-/whitespace-obfuscated forms (`jav&#x09;ascript:`).
- Script-capable or remote CSS: `expression()`, `-moz-binding`, `behavior:`, `@import`, and any
  `url(...)` — so inline styles can't fetch remotely (tracking) or execute.
- Any other unrecognised tag is **unwrapped**: the tag is dropped but its text content is kept.

The receiving extension should still insert content safely (e.g. as text, or through its own sanitizer)
as defense in depth — a server-side sanitizer can't perfectly mirror the browser's HTML parser.

## Notes

- `protocol` in `/ping` is the API version; bump-aware clients should check it.
- The tree is served from an in-memory snapshot refreshed when templates change, so it stays current
  without you polling aggressively — re-fetch `/tree` when you open your menu.
