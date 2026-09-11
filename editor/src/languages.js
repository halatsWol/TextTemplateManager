// Shared syntax-highlighting setup. The editor highlights live via lowlight (a ProseMirror
// integration); the read-only preview re-highlights stored HTML with highlight.js core. Both
// register the exact same grammars so the token classes — and therefore the CSS colors — match.
import { createLowlight } from 'lowlight'
import hljs from 'highlight.js/lib/core'

import powershell from 'highlight.js/lib/languages/powershell'
import python from 'highlight.js/lib/languages/python'
import dos from 'highlight.js/lib/languages/dos'
import bash from 'highlight.js/lib/languages/bash'
import json from 'highlight.js/lib/languages/json'
import javascript from 'highlight.js/lib/languages/javascript'
import typescript from 'highlight.js/lib/languages/typescript'
import xml from 'highlight.js/lib/languages/xml'
import sql from 'highlight.js/lib/languages/sql'
import csharp from 'highlight.js/lib/languages/csharp'
import yaml from 'highlight.js/lib/languages/yaml'
import markdown from 'highlight.js/lib/languages/markdown'

// A real no-op grammar so "Plain text" means no highlighting: without a registered language the
// extension falls back to auto-detection, so an empty language could never truly turn colors off.
// `disableAutodetect` also keeps it out of the "Auto" detection candidates.
const plaintext = () => ({ name: 'Plain text', disableAutodetect: true, contains: [] })

const GRAMMARS = { plaintext, powershell, python, dos, bash, json, javascript, typescript, xml, sql, csharp, yaml, markdown }

export const lowlight = createLowlight(GRAMMARS)
for (const [name, def] of Object.entries(GRAMMARS)) hljs.registerLanguage(name, def)

// The language a code block has before the user picks one. A block left at this value renders
// without a `language-` class (see renderHTML), so both the editor and preview must resolve a
// class-less block to this same default.
export const DEFAULT_LANGUAGE = 'auto'

// Picker options; `value` is the id stored on the node. 'auto' = detect the language, 'plaintext'
// = no highlighting. New blocks default to DEFAULT_LANGUAGE.
export const LANGUAGES = [
    { value: 'auto',       label: 'Auto-detect' },
    { value: 'plaintext',  label: 'Plain text' },
    { value: 'powershell', label: 'PowerShell' },
    { value: 'python',     label: 'Python' },
    { value: 'dos',        label: 'Batch / cmd' },
    { value: 'bash',       label: 'Bash / Shell' },
    { value: 'json',       label: 'JSON' },
    { value: 'javascript', label: 'JavaScript' },
    { value: 'typescript', label: 'TypeScript' },
    { value: 'xml',        label: 'HTML / XML' },
    { value: 'sql',        label: 'SQL' },
    { value: 'csharp',     label: 'C#' },
    { value: 'yaml',       label: 'YAML' },
    { value: 'markdown',   label: 'Markdown' },
]

const escapeHtml = (s) => s.replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]))

// Highlight raw code to an HTML string of <span class="hljs-*"> tokens (used by the preview).
// 'auto' detects the language; 'plaintext'/unknown fall back to escaped plain text.
export function highlightToHtml(text, lang) {
    try {
        if (lang === 'auto') return hljs.highlightAuto(text).value
        if (lang && lang !== 'plaintext' && hljs.getLanguage(lang)) {
            return hljs.highlight(text, { language: lang, ignoreIllegals: true }).value
        }
    } catch (_) { /* fall through to plain */ }
    return escapeHtml(text)
}
