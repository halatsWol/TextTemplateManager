// Read-only preview highlighter. The stored HTML carries only the raw code plus a `language-xxx`
// class on <code>; regenerate the highlight token spans on each render so the preview's colors
// match the editor's. Exposed on window for preview.html's setContent to call.
import { highlightToHtml, DEFAULT_LANGUAGE } from './languages.js'

function highlightAll(root) {
    root.querySelectorAll('pre > code').forEach((code) => {
        const cls = [...code.classList].find((c) => c.startsWith('language-'))
        // A class-less block is one left at the default (matches the editor's defaultLanguage).
        const lang = cls ? cls.slice('language-'.length) : DEFAULT_LANGUAGE
        code.innerHTML = highlightToHtml(code.textContent, lang)
    })
}

window.previewHighlight = highlightAll
