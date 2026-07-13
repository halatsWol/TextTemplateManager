import { Editor, Node, mergeAttributes } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import Underline from '@tiptap/extension-underline'
import TextStyle from '@tiptap/extension-text-style'
import { Color } from '@tiptap/extension-color'
import Highlight from '@tiptap/extension-highlight'
import Table from '@tiptap/extension-table'
import TableRow from '@tiptap/extension-table-row'
import TableCell from '@tiptap/extension-table-cell'
import TableHeader from '@tiptap/extension-table-header'
import ListItem from '@tiptap/extension-list-item'
import Link from '@tiptap/extension-link'
import TextAlign from '@tiptap/extension-text-align'
import Subscript from '@tiptap/extension-subscript'
import Superscript from '@tiptap/extension-superscript'
import CodeBlock from '@tiptap/extension-code-block'
import { Fragment, Slice } from '@tiptap/pm/model'
import { TextSelection, EditorState } from '@tiptap/pm/state'

// Allow any block (incl. headings) inside a list item so heading + list can coexist.
const RichListItem = ListItem.extend({ content: 'block+' })

// Code block with an optional line-number gutter. The `lineNumbers` attribute lets one node
// serve both the plain "Preformat" style (off) and the "Code block" (on). The gutter is a
// contenteditable=false sibling drawn by a nodeView; it is never part of the stored HTML.
const LineNumberCodeBlock = CodeBlock.extend({
    addAttributes() {
        return {
            ...this.parent?.(),
            lineNumbers: {
                default: false,
                parseHTML: el => el.getAttribute('data-line-numbers') === 'true',
                renderHTML: attrs => (attrs.lineNumbers ? { 'data-line-numbers': 'true' } : {}),
            },
        }
    },
    addNodeView() {
        return ({ node }) => {
            const pre = document.createElement('pre')
            const gutter = document.createElement('span')
            gutter.className = 'code-gutter'
            gutter.contentEditable = 'false'
            const code = document.createElement('code')
            pre.appendChild(gutter)
            pre.appendChild(code)

            const sync = (n) => {
                const on = !!n.attrs.lineNumbers
                pre.classList.toggle('with-line-numbers', on)
                if (on) {
                    const lines = (n.textContent.match(/\n/g) || []).length + 1
                    let s = ''
                    for (let i = 1; i <= lines; i++) s += (i > 1 ? '\n' : '') + i
                    gutter.textContent = s
                }
            }
            sync(node)

            return {
                dom: pre,
                contentDOM: code,
                update: (updated) => {
                    if (updated.type.name !== node.type.name) return false
                    sync(updated)
                    return true
                },
            }
        }
    },
})

// Jira-style callout panel (info / note / success / warning / error). Serialized as
// <div class="ttm-panel ak-editor-panel" data-panel-type="TYPE"> so Atlassian's editor
// reconstructs it on paste — its panel node parses div[data-panel-type]. The colored
// background and leading icon are CSS-only, never part of the stored/pasted HTML.
const PANEL_TYPES = ['info', 'note', 'success', 'warning', 'error']
const Panel = Node.create({
    name: 'panel',
    group: 'block',
    content: 'block+',
    defining: true,
    addAttributes() {
        return {
            panelType: {
                default: 'info',
                parseHTML: el => {
                    const t = el.getAttribute('data-panel-type')
                    return PANEL_TYPES.includes(t) ? t : 'info'
                },
                renderHTML: attrs => ({ 'data-panel-type': attrs.panelType }),
            },
        }
    },
    parseHTML() { return [{ tag: 'div[data-panel-type]' }] },
    renderHTML({ HTMLAttributes }) {
        return ['div', mergeAttributes(HTMLAttributes, { class: 'ttm-panel ak-editor-panel' }), 0]
    },
    addCommands() {
        return {
            // Wrap the current block(s) in a panel, or unwrap/retype an existing one.
            togglePanel: (panelType) => ({ commands, editor }) => {
                if (editor.isActive('panel', { panelType })) return commands.lift('panel')
                if (editor.isActive('panel')) return commands.updateAttributes('panel', { panelType })
                return commands.wrapIn('panel', { panelType })
            },
        }
    },
})

// Remove hard-break (<br>) nodes at the start/end of a text block's inline content.
function trimInlineBreaks(inline) {
    const nodes = []
    inline.forEach(n => nodes.push(n))
    while (nodes.length && nodes[nodes.length - 1].type.name === 'hardBreak') nodes.pop()
    while (nodes.length && nodes[0].type.name === 'hardBreak') nodes.shift()
    return Fragment.fromArray(nodes)
}

// Recursively clean a fragment:
//  - drop hard-break nodes that are SIBLINGS OF BLOCK nodes (junk that ProseMirror's
//    clipboard parser adds around a pasted block; these become empty <p><br></p> blocks),
//  - trim leading/trailing hard breaks inside each text block.
// A pure-inline fragment (no block siblings) keeps its hard breaks — they're real line breaks.
function stripBoundaryBreaks(fragment) {
    let hasBlock = false
    fragment.forEach(n => { if (n.isBlock) hasBlock = true })

    const out = []
    fragment.forEach(node => {
        if (node.type.name === 'hardBreak') {
            if (!hasBlock) out.push(node) // legitimate inline line break
            // else: drop it (sibling-of-block junk break)
        } else if (node.isTextblock) {
            out.push(node.copy(trimInlineBreaks(node.content)))
        } else if (node.isBlock && node.content.childCount) {
            out.push(node.copy(stripBoundaryBreaks(node.content)))
        } else {
            out.push(node)
        }
    })
    return Fragment.fromArray(out)
}

// ---- Bridge to the WinUI host -------------------------------------------------
const host = window.chrome && window.chrome.webview ? window.chrome.webview : null
const post = (msg) => { try { host && host.postMessage(JSON.stringify(msg)) } catch (_) {} }

let suppressChange = false
const debounce = (fn, ms) => {
    let t
    return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms) }
}
const emitChange = debounce(() => {
    if (!suppressChange) post({ type: 'change', html: getCleanHtml() })
}, 250)

// ---- Editor -------------------------------------------------------------------
const editor = new Editor({
    element: document.querySelector('#editor'),
    extensions: [
        StarterKit.configure({ heading: { levels: [1, 2, 3, 4, 5, 6] }, listItem: false, codeBlock: false }),
        RichListItem,
        LineNumberCodeBlock,
        Underline,
        TextStyle,
        Color,
        Highlight.configure({ multicolor: true }),
        Table.configure({ resizable: true }),
        TableRow,
        TableHeader,
        TableCell,
        Link.configure({
            openOnClick: false,          // clicking a link in the editor edits it, never navigates
            autolink: true,
            linkOnPaste: true,
            HTMLAttributes: { rel: 'noopener noreferrer nofollow', target: '_blank' },
        }),
        TextAlign.configure({ types: ['heading', 'paragraph'] }),
        Subscript,
        Superscript,
        Panel,
    ],
    content: '',
    editorProps: {
        // Enable the browser spell checker so WebView2's context menu offers suggestions.
        attributes: { spellcheck: 'true' },
        handleDOMEvents: {
            // Right-click should place the caret under the pointer so the spell-check menu targets
            // the word you clicked. Chromium doesn't move the caret mid-word on right-click inside
            // ProseMirror, so suggestions only showed when clicking a word edge. Keep an existing
            // selection if the click is inside it (so Copy/Cut still act on the selection).
            contextmenu(view, event) {
                const at = view.posAtCoords({ left: event.clientX, top: event.clientY })
                if (!at) return false
                const { from, to } = view.state.selection
                if (from !== to && at.pos > from && at.pos < to) return false
                view.dispatch(view.state.tr.setSelection(TextSelection.near(view.state.doc.resolve(at.pos))))
                return false
            },
        },
        // Clean pasted HTML from EXTERNAL sources (stray <br> runs before a block close).
        transformPastedHTML(html) {
            return html
                .replace(/(?:\s*<br\s*\/?>\s*)+(<\/(?:p|h[1-6]|li|div)>)/gi, '$1')
                .replace(/(?:<br\s*\/?>\s*){2,}/gi, '<br>')
        },
        // Clean pasted content at the NODE level — this also covers editor→editor paste, which
        // uses ProseMirror's internal slice (not the HTML), so transformPastedHTML can't reach it.
        transformPasted(slice) {
            return new Slice(stripBoundaryBreaks(slice.content), slice.openStart, slice.openEnd)
        },
        handleKeyDown(view, event) {
            // Tab in plain text inserts spaces; inside lists StarterKit handles nesting.
            if (event.key === 'Tab') {
                const inList = editor.isActive('listItem')
                if (!inList) {
                    event.preventDefault()
                    if (!event.shiftKey) editor.commands.insertContent('    ')
                    return true
                }
            }
            return false
        },
    },
    onCreate() { refreshToolbar(); post({ type: 'ready' }) },
    onUpdate() { emitChange(); refreshToolbar() },
    onSelectionUpdate() { refreshToolbar() },
})

// ---- Public API called from C# ------------------------------------------------
// Strip junk hard breaks: runs of <br> and a trailing <br> right before a block closes.
// Keeps intentional single mid-line breaks.
function normalizeBreaks(html) {
    return (html || '<p></p>')
        .replace(/(?:<br\s*\/?>\s*)+(<\/(?:p|h[1-6]|li|div|td|th)>)/gi, '$1')
        .replace(/(?:<br\s*\/?>\s*){2,}/gi, '<br>')
}

// Inline formatting tags whose boundary whitespace should live OUTSIDE the tag.
const INLINE_MARK_SEL = 'strong,b,em,i,u,s,del,code,mark,span,a,sub,sup'

function firstTextNode(el) {
    const w = document.createTreeWalker(el, NodeFilter.SHOW_TEXT)
    return w.nextNode()
}
function lastTextNode(el) {
    const w = document.createTreeWalker(el, NodeFilter.SHOW_TEXT)
    let last = null, n
    while ((n = w.nextNode())) last = n
    return last
}

// Move leading/trailing whitespace OUT of an inline mark (one pass over `root`).
// e.g. "<strong>bold </strong>" -> "<strong>bold</strong> ". Markdown/RTF/HTML all
// require the character next to a mark boundary to be non-whitespace to render.
function hoistBoundaryWhitespaceOnce(root) {
    let changed = false
    root.querySelectorAll(INLINE_MARK_SEL).forEach(el => {
        if (!el.parentNode) return

        const ft = firstTextNode(el)
        if (ft) {
            const m = /^[ \t\r\n]+/.exec(ft.data)
            if (m) {
                ft.data = ft.data.slice(m[0].length)
                el.parentNode.insertBefore(document.createTextNode(m[0]), el)
                changed = true
            }
        }

        const lt = lastTextNode(el)
        if (lt) {
            const m = /[ \t\r\n]+$/.exec(lt.data)
            if (m) {
                lt.data = lt.data.slice(0, lt.data.length - m[0].length)
                el.parentNode.insertBefore(document.createTextNode(m[0]), el.nextSibling)
                changed = true
            }
        }

        // A mark left holding only whitespace collapses away, unless it carries a real
        // void child (<br>/<img>).
        if (el.textContent === '' && !el.querySelector('br,img')) {
            el.remove()
            changed = true
        }
    })
    return changed
}

// Normalize the OUTPUT html only (operates on a detached template, never the live editor
// doc) so mark boundaries are clean without ever disturbing typing or the caret. Loops a
// few times so whitespace cascades out of nested marks.
function normalizeMarkBoundaries(html) {
    const tpl = document.createElement('template')
    tpl.innerHTML = html
    let i = 0
    while (i++ < 6 && hoistBoundaryWhitespaceOnce(tpl.content)) { /* until stable */ }
    return tpl.innerHTML
}

function getCleanHtml() { return normalizeMarkBoundaries(editor.getHTML()) }

window.editorApi = {
    setContent(html) {
        suppressChange = true
        editor.commands.setContent(normalizeBreaks(html), false)
        // History is per template: discard undo/redo so "back" can't cross into the previously
        // loaded template's content. Recreating the state re-inits the history plugin (empty
        // stacks), making this content the baseline / first entry.
        editor.view.updateState(EditorState.create({
            doc: editor.state.doc,
            plugins: editor.state.plugins,
        }))
        suppressChange = false
    },
    getContent() { return getCleanHtml() },
    focus() { editor.commands.focus() },
    setTheme(dark) { document.body.classList.toggle('dark', !!dark) },
    setEditable(on) { editor.setEditable(!!on) },
}

// ---- Toolbar ------------------------------------------------------------------
const toolbar = document.querySelector('#toolbar')
const run = (fn) => { fn(editor.chain().focus()).run() }

// Clean inline SVG icons (feather-style, stroke = currentColor so they follow the theme).
const svg = (inner, extra = '') =>
    `<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" ${extra}>${inner}</svg>`
const ICONS = {
    undo: svg('<polyline points="1 4 1 10 7 10"/><path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10"/>'),
    redo: svg('<polyline points="23 4 23 10 17 10"/><path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10"/>'),
    type: svg('<polyline points="4 7 4 4 20 4 20 7"/><line x1="9" y1="20" x2="15" y2="20"/><line x1="12" y1="4" x2="12" y2="20"/>'),
    bullet: svg('<line x1="9" y1="6" x2="20" y2="6"/><line x1="9" y1="12" x2="20" y2="12"/><line x1="9" y1="18" x2="20" y2="18"/><line x1="4" y1="6" x2="4.01" y2="6"/><line x1="4" y1="12" x2="4.01" y2="12"/><line x1="4" y1="18" x2="4.01" y2="18"/>'),
    ordered: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="10" y1="6" x2="20" y2="6"/><line x1="10" y1="12" x2="20" y2="12"/><line x1="10" y1="18" x2="20" y2="18"/><text x="1.5" y="8" font-size="7" fill="currentColor" stroke="none" font-family="sans-serif">1</text><text x="1.5" y="14" font-size="7" fill="currentColor" stroke="none" font-family="sans-serif">2</text><text x="1.5" y="20" font-size="7" fill="currentColor" stroke="none" font-family="sans-serif">3</text></svg>',
    textColor: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M6 17 11 6 16 17"/><line x1="7.5" y1="13" x2="14.5" y2="13"/><rect x="4" y="19.5" width="14" height="2.6" rx="1" fill="currentColor" stroke="none"/></svg>',
    highlight: svg('<path d="M12 20h9"/><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4Z"/>'),
    table: svg('<rect x="3" y="3" width="18" height="18" rx="1"/><line x1="3" y1="9" x2="21" y2="9"/><line x1="3" y1="15" x2="21" y2="15"/><line x1="9" y1="3" x2="9" y2="21"/><line x1="15" y1="3" x2="15" y2="21"/>'),
    link: svg('<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>'),
    code: svg('<polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/>'),
    hr: svg('<line x1="3" y1="12" x2="21" y2="12"/>'),
    clear: svg('<path d="M4 7V5h14v2"/><line x1="10" y1="5" x2="7" y2="19"/><line x1="6" y1="19" x2="12" y2="19"/><line x1="15" y1="13" x2="21" y2="19"/><line x1="21" y1="13" x2="15" y2="19"/>'),
    alignLeft: svg('<line x1="3" y1="6" x2="21" y2="6"/><line x1="3" y1="12" x2="15" y2="12"/><line x1="3" y1="18" x2="18" y2="18"/>'),
    alignCenter: svg('<line x1="3" y1="6" x2="21" y2="6"/><line x1="6" y1="12" x2="18" y2="12"/><line x1="4" y1="18" x2="20" y2="18"/>'),
    alignRight: svg('<line x1="3" y1="6" x2="21" y2="6"/><line x1="9" y1="12" x2="21" y2="12"/><line x1="6" y1="18" x2="21" y2="18"/>'),
    alignJustify: svg('<line x1="3" y1="6" x2="21" y2="6"/><line x1="3" y1="12" x2="21" y2="12"/><line x1="3" y1="18" x2="21" y2="18"/>'),
    codeBlock: svg('<rect x="2" y="4" width="20" height="16" rx="2"/><polyline points="8 10 6 12 8 14"/><polyline points="16 10 18 12 16 14"/><line x1="13" y1="9" x2="11" y2="15"/>'),
    symbol: svg('<circle cx="12" cy="12" r="10"/><path d="M8 14s1.5 2 4 2 4-2 4-2"/><line x1="9" y1="9" x2="9.01" y2="9"/><line x1="15" y1="9" x2="15.01" y2="9"/>'),
    dateTime: svg('<rect x="3" y="4" width="18" height="18" rx="2"/><line x1="16" y1="2" x2="16" y2="6"/><line x1="8" y1="2" x2="8" y2="6"/><line x1="3" y1="10" x2="21" y2="10"/><polyline points="12 13 12 16 14 16"/>'),
    // Filled quote glyph (Material "format_quote"), vertically centered in the 24x24 box.
    quote: '<svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M6 17h3l2-4V7H5v6h3zm8 0h3l2-4V7h-6v6h3z"/></svg>',
    // Callout panel: a box with a filled left rail (evokes the colored bar of a panel).
    panel: svg('<rect x="3" y="4" width="18" height="16" rx="2"/><rect x="3" y="4" width="4" height="16" rx="1" fill="currentColor" stroke="none"/><line x1="10" y1="9" x2="18" y2="9"/><line x1="10" y1="13" x2="18" y2="13"/>'),
}

function button(html, title, onClick, isActive) {
    const b = document.createElement('button')
    b.className = 'tb-btn'
    b.innerHTML = html
    b.title = title
    b.onclick = (e) => { e.preventDefault(); onClick() }
    if (isActive) b._active = isActive
    return b
}

function sep() {
    const s = document.createElement('div')
    s.className = 'tb-sep'
    return s
}

const activeChecks = []
function refreshToolbar() {
    for (const { el, fn } of activeChecks) el.classList.toggle('active', !!fn())
}

// Popups: only one open at a time.
let openPopup = null
function closePopup() { if (openPopup) { openPopup.classList.add('hidden'); openPopup = null } }
document.addEventListener('click', (e) => {
    if (openPopup && !openPopup.contains(e.target) && !e.target.closest('.tb-btn[data-pop]')) closePopup()
})

// Show `pop` under `btn`, clamped to the viewport so a right-edge button (e.g. the symbol
// picker) doesn't push the popup off-screen.
function placePopup(pop, btn) {
    pop.classList.remove('hidden')
    const rect = btn.getBoundingClientRect()
    const left = Math.max(6, Math.min(rect.left, window.innerWidth - pop.offsetWidth - 6))
    pop.style.left = left + 'px'
}

function popupButton(html, title, buildContent) {
    const b = button(html + '<span class="caret">▾</span>', title, () => {})
    b.dataset.pop = '1'
    const pop = document.createElement('div')
    pop.className = 'popup hidden'
    buildContent(pop, () => closePopup())
    b.onclick = (e) => {
        e.preventDefault()
        const wasOpen = openPopup === pop
        closePopup()
        if (!wasOpen) {
            placePopup(pop, b)
            openPopup = pop
        }
    }
    toolbar.appendChild(b)
    toolbar.appendChild(pop)
    return b
}

function addActive(el, fn) { activeChecks.push({ el, fn }); return el }

// Undo / redo
toolbar.appendChild(button(ICONS.undo, 'Undo', () => run(c => c.undo())))
toolbar.appendChild(button(ICONS.redo, 'Redo', () => run(c => c.redo())))
toolbar.appendChild(sep())

// Format (Normal + Heading 1..6 + Preformat) = 7 text styles + preformat
popupButton(ICONS.type, 'Text format', (pop, close) => {
    const item = (label, onClick) => {
        const el = document.createElement('button')
        el.className = 'menu-item'
        el.textContent = label
        el.onclick = (e) => { e.preventDefault(); onClick(); close() }
        pop.appendChild(el)
    }
    const menuSep = () => { const d = document.createElement('div'); d.className = 'menu-sep'; pop.appendChild(d) }
    item('Normal', () => run(c => c.setParagraph()))
    menuSep()
    for (let lvl = 1; lvl <= 6; lvl++) item('Heading ' + lvl, () => run(c => c.toggleHeading({ level: lvl })))
    menuSep()
    item('Preformat', () => run(c => c.toggleCodeBlock({ lineNumbers: false })))
})
toolbar.appendChild(sep())

// Inline styles
toolbar.appendChild(addActive(button('<b>B</b>', 'Bold', () => run(c => c.toggleBold())), () => editor.isActive('bold')))
toolbar.appendChild(addActive(button('<i>I</i>', 'Italic', () => run(c => c.toggleItalic())), () => editor.isActive('italic')))
toolbar.appendChild(addActive(button('<u>U</u>', 'Underline', () => run(c => c.toggleUnderline())), () => editor.isActive('underline')))
toolbar.appendChild(addActive(button('<s>S</s>', 'Strikethrough', () => run(c => c.toggleStrike())), () => editor.isActive('strike')))
toolbar.appendChild(addActive(button(ICONS.code, 'Inline code', () => run(c => c.toggleCode())), () => editor.isActive('code')))
toolbar.appendChild(addActive(button('<span class="sx">x<sup>2</sup></span>', 'Superscript', () => run(c => c.toggleSuperscript())), () => editor.isActive('superscript')))
toolbar.appendChild(addActive(button('<span class="sx">x<sub>2</sub></span>', 'Subscript', () => run(c => c.toggleSubscript())), () => editor.isActive('subscript')))
toolbar.appendChild(sep())

// Lists
toolbar.appendChild(addActive(button(ICONS.bullet, 'Bullet list', () => run(c => c.toggleBulletList())), () => editor.isActive('bulletList')))
toolbar.appendChild(addActive(button(ICONS.ordered, 'Numbered list', () => run(c => c.toggleOrderedList())), () => editor.isActive('orderedList')))
toolbar.appendChild(sep())

// Colors
const TEXT_COLORS = [
    ['Black', '#000000'], ['Dark Gray', '#404040'], ['Gray', '#808080'], ['Light Gray', '#c8c8c8'], ['White', '#ffffff'],
    ['Dark Red', '#8b0000'], ['Red', '#e81123'], ['Orange', '#ff8c00'], ['Amber', '#ffb900'], ['Yellow', '#fff100'],
    ['Dark Green', '#006400'], ['Green', '#107c10'], ['Teal', '#00b294'], ['Lime', '#00cc6a'], ['Cyan', '#00b7c3'],
    ['Dark Blue', '#00008b'], ['Blue', '#0078d7'], ['Light Blue', '#69b7eb'], ['Purple', '#881798'], ['Pink', '#e3008c'],
]
const HIGHLIGHT_COLORS = [
    ['Yellow', '#fff100'], ['Green', '#00ff00'], ['Cyan', '#00ffff'], ['Pink', '#ff80ff'], ['Orange', '#ffb900'],
    ['Red', '#e81123'], ['Gray', '#c0c0c0'], ['Black', '#000000'], ['Blue', '#69b7eb'], ['Lime', '#b6ff00'],
]

function colorPopup(colors, resetLabel, apply, reset) {
    return (pop, close) => {
        const grid = document.createElement('div')
        grid.className = 'swatches'
        for (const [name, hex] of colors) {
            const sw = document.createElement('div')
            sw.className = 'swatch'
            sw.style.background = hex
            sw.title = name
            sw.onclick = (e) => { e.preventDefault(); apply(hex); close() }
            grid.appendChild(sw)
        }
        pop.appendChild(grid)

        const resetBtn = document.createElement('button')
        resetBtn.className = 'popup-action'
        resetBtn.textContent = resetLabel
        resetBtn.onclick = (e) => { e.preventDefault(); reset(); close() }
        pop.appendChild(resetBtn)
    }
}

popupButton(ICONS.textColor, 'Text color', colorPopup(
    TEXT_COLORS, 'Automatic',
    (hex) => run(c => c.setColor(hex)),
    () => run(c => c.unsetColor()),
))
popupButton(ICONS.highlight, 'Highlight color', colorPopup(
    HIGHLIGHT_COLORS, 'None',
    (hex) => run(c => c.setHighlight({ color: hex })),
    () => run(c => c.unsetHighlight()),
))
toolbar.appendChild(sep())

// Table: 8x8 hover grid + in-table actions
popupButton(ICONS.table, 'Insert table', (pop, close) => {
    const label = document.createElement('div')
    label.className = 'table-label'
    label.textContent = 'Insert table'
    pop.appendChild(label)

    const N = 8
    const grid = document.createElement('div')
    grid.className = 'table-grid'
    const cells = []
    for (let r = 0; r < N; r++) {
        for (let col = 0; col < N; col++) {
            const cell = document.createElement('div')
            cell.className = 'table-cell'
            cell.onmouseenter = () => {
                for (let i = 0; i < cells.length; i++) {
                    const cr = Math.floor(i / N), cc = i % N
                    cells[i].classList.toggle('on', cr <= r && cc <= col)
                }
                label.textContent = (r + 1) + ' × ' + (col + 1) + ' table'
            }
            cell.onclick = (e) => {
                e.preventDefault()
                run(c => c.insertTable({ rows: r + 1, cols: col + 1, withHeaderRow: false }))
                close()
            }
            cells.push(cell)
            grid.appendChild(cell)
        }
    }
    pop.appendChild(grid)

    const actions = document.createElement('div')
    actions.className = 'table-actions'
    const act = (label, fn) => {
        const el = document.createElement('button')
        el.className = 'menu-item'
        el.textContent = label
        el.onclick = (e) => { e.preventDefault(); run(fn); close() }
        actions.appendChild(el)
    }
    act('+ Row', c => c.addRowAfter())
    act('+ Column', c => c.addColumnAfter())
    act('- Row', c => c.deleteRow())
    act('- Column', c => c.deleteColumn())
    act('Delete table', c => c.deleteTable())
    pop.appendChild(actions)
})
toolbar.appendChild(sep())

// Link: popup with a Text field and a URL field. Prefills from the selection — a selected URL
// goes to the Link field, other selected text to the Text field — and reuses an existing link's
// href/text. On apply an empty Text field falls back to the URL. Pasting a URL onto a selection
// still turns it into a link (handled by the Link extension, unchanged).
;(function addLinkControl() {
    const b = button(ICONS.link + '<span class="caret">▾</span>', 'Link', () => {})
    b.dataset.pop = '1'
    const pop = document.createElement('div')
    pop.className = 'popup hidden link-popup'

    const textInput = document.createElement('input')
    textInput.type = 'text'
    textInput.className = 'link-input'
    textInput.placeholder = 'Text'

    const urlInput = document.createElement('input')
    urlInput.type = 'text'
    urlInput.className = 'link-input'
    urlInput.placeholder = 'https://example.com'

    const row = document.createElement('div')
    row.className = 'link-actions'
    const applyBtn = document.createElement('button')
    applyBtn.className = 'menu-item'
    applyBtn.textContent = 'Apply'
    const removeBtn = document.createElement('button')
    removeBtn.className = 'menu-item'
    removeBtn.textContent = 'Remove'
    row.appendChild(applyBtn)
    row.appendChild(removeBtn)
    pop.appendChild(textInput)
    pop.appendChild(urlInput)
    pop.appendChild(row)

    // A URL is a known scheme (http/ftp/mailto/tel/…) or an obvious bare domain; never has spaces.
    const isUrl = (s) => {
        s = (s || '').trim()
        if (!s || /\s/.test(s)) return false
        return /^(https?|ftp|ftps|mailto|tel):/i.test(s) ||
               /^(www\.)?[a-z0-9-]+(\.[a-z0-9-]+)+([\/?#].*)?$/i.test(s)
    }
    const normalizeUrl = (s) => {
        s = s.trim()
        return /^[a-z][a-z0-9+.-]*:/i.test(s) ? s : 'https://' + s  // bare domain -> https
    }
    const selectionText = () => {
        const { from, to, empty } = editor.state.selection
        return empty ? '' : editor.state.doc.textBetween(from, to, ' ')
    }

    const apply = () => {
        const rawUrl = urlInput.value.trim()
        if (!rawUrl) {                                   // no URL -> clear any link on the selection
            run(c => c.extendMarkRange('link').unsetLink())
            closePopup()
            return
        }
        const href = normalizeUrl(rawUrl)
        const text = textInput.value.trim()
        const selEmpty = editor.state.selection.empty
        const chain = editor.chain().focus()

        if (!selEmpty && (text === '' || text === selectionText())) {
            // Keep the selected text as-is and just link it (preserves its inline formatting).
            chain.extendMarkRange('link').setLink({ href }).run()
        } else {
            // Insert/replace with explicit link text (empty Text falls back to the URL).
            if (selEmpty && editor.isActive('link')) chain.extendMarkRange('link')
            chain.insertContent({ type: 'text', text: text || rawUrl, marks: [{ type: 'link', attrs: { href } }] })
            if (selEmpty && !editor.isActive('link')) chain.insertContent(' ')
            chain.run()
        }
        closePopup()
    }
    applyBtn.onclick = (e) => { e.preventDefault(); apply() }
    removeBtn.onclick = (e) => { e.preventDefault(); run(c => c.extendMarkRange('link').unsetLink()); closePopup() }
    const onEnter = (e) => { if (e.key === 'Enter') { e.preventDefault(); apply() } }
    textInput.onkeydown = onEnter
    urlInput.onkeydown = onEnter

    b.onclick = (e) => {
        e.preventDefault()
        const wasOpen = openPopup === pop
        closePopup()
        if (!wasOpen) {
            const href = editor.getAttributes('link').href || ''
            const selText = selectionText()
            if (href) {                        // editing an existing link
                urlInput.value = href
                textInput.value = selText
            } else if (isUrl(selText)) {       // selected a URL -> it's the link
                urlInput.value = selText
                textInput.value = ''
            } else {                           // selected plain text -> it's the display text
                textInput.value = selText
                urlInput.value = ''
            }
            placePopup(pop, b)
            openPopup = pop
            setTimeout(() => (urlInput.value ? textInput : urlInput).focus(), 0)
        }
    }
    toolbar.appendChild(b)
    toolbar.appendChild(pop)
    addActive(b, () => editor.isActive('link'))
})()

// Blockquote + code block + horizontal rule
toolbar.appendChild(addActive(button(ICONS.quote, 'Quote', () => run(c => c.toggleBlockquote())), () => editor.isActive('blockquote')))
toolbar.appendChild(addActive(button(ICONS.codeBlock, 'Code block', () => run(c => c.toggleCodeBlock({ lineNumbers: true }))), () => editor.isActive('codeBlock')))
toolbar.appendChild(button(ICONS.hr, 'Divider', () => run(c => c.setHorizontalRule())))

// Callout panels (info / note / success / warning / error). Choosing the active type again
// removes the panel. Pastes into Jira as a matching panel via the HTML/Jira paste mode.
popupButton(ICONS.panel, 'Panel', (pop, close) => {
    const item = (label, type) => {
        const el = document.createElement('button')
        el.className = 'menu-item panel-item panel-item-' + type
        el.innerHTML = '<span class="panel-dot"></span>' + label
        el.onclick = (e) => { e.preventDefault(); run(c => c.togglePanel(type)); close() }
        pop.appendChild(el)
    }
    item('Info', 'info')
    item('Note', 'note')
    item('Success', 'success')
    item('Warning', 'warning')
    item('Error', 'error')
})
toolbar.appendChild(sep())

// Text alignment
popupButton(ICONS.alignLeft, 'Text alignment', (pop, close) => {
    const alignRow = document.createElement('div')
    alignRow.className = 'align-row'
    const opt = (icon, value, title) => {
        const el = document.createElement('button')
        el.className = 'tb-btn'
        el.innerHTML = icon
        el.title = title
        el.onclick = (e) => { e.preventDefault(); run(c => c.setTextAlign(value)); close() }
        alignRow.appendChild(el)
    }
    opt(ICONS.alignLeft, 'left', 'Align left')
    opt(ICONS.alignCenter, 'center', 'Align center')
    opt(ICONS.alignRight, 'right', 'Align right')
    opt(ICONS.alignJustify, 'justify', 'Justify')
    pop.appendChild(alignRow)
})

// Clear formatting (remove inline marks + reset block type to a normal paragraph)
toolbar.appendChild(button(ICONS.clear, 'Clear formatting', () => run(c => c.unsetAllMarks().clearNodes())))
toolbar.appendChild(sep())

// Insert symbol: grid of common glyphs inserted as plain Unicode text (pastes everywhere).
// Note: Windows' Segoe UI Emoji renders ✔️/✖️/☑️ in PURPLE — avoid them. ✅ (green) and
// ❌ (red) are the reliably red/green check & cross.
const SYMBOLS = [
    ['✅', 'Check'], ['❌', 'Cross'], ['⬜', 'Empty box'], ['⚠️', 'Warning'], ['❗', 'Exclamation'], ['❓', 'Question'], ['ℹ️', 'Info'], ['🟢', 'Green circle'],
    ['🔴', 'Red circle'], ['🟡', 'Yellow circle'], ['⚡', 'High voltage'], ['🔥', 'Fire'], ['⭐', 'Star'], ['💡', 'Idea'], ['👍', 'Thumbs up'], ['👎', 'Thumbs down'],
]

popupButton(ICONS.symbol, 'Insert symbol', (pop, close) => {
    const grid = document.createElement('div')
    grid.className = 'symbol-grid'
    for (const [ch, name] of SYMBOLS) {
        const el = document.createElement('button')
        el.className = 'symbol'
        el.textContent = ch
        el.title = name
        el.onclick = (e) => { e.preventDefault(); run(c => c.insertContent(ch)); close() }
        grid.appendChild(el)
    }
    pop.appendChild(grid)
})

// Insert a date/time placeholder that resolves to the ACTUAL current date/time on paste.
popupButton(ICONS.dateTime, 'Insert date / time', (pop, close) => {
    const item = (label, placeholder) => {
        const el = document.createElement('button')
        el.className = 'menu-item'
        el.textContent = label
        el.onclick = (e) => { e.preventDefault(); run(c => c.insertContent(placeholder)); close() }
        pop.appendChild(el)
    }
    item('Date (2026-07-10)', '$[DATE(YYYY-MM-DD)]')
    item('Time (14:30)', '$[TIME(HH:mm)]')
    item('Date & time', '$[DATE(YYYY-MM-DD)] $[TIME(HH:mm)]')
    item('Year', '$[YEAR]')
})

// Report the very first ready state (in case onCreate fired before this file finished)
post({ type: 'ready' })
