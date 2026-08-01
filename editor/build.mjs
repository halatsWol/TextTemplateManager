import * as esbuild from 'esbuild'
import { cpSync, mkdirSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
// Output goes into the app's Assets so it ships with the build.
const outDir = resolve(here, '..', 'Assets', 'editor')
mkdirSync(outDir, { recursive: true })

await esbuild.build({
  entryPoints: [resolve(here, 'src', 'main.js')],
  bundle: true,
  format: 'iife',
  minify: true,
  target: ['chrome110'],
  outfile: resolve(outDir, 'editor.bundle.js'),
  logLevel: 'info',
})

// Copied verbatim — no cache-busting query needed. MainPage inlines editor.css and editor.bundle.js
// into this HTML before handing it to WebView2, so the asset URLs are stripped and never fetched.
// A timestamped ?v= only made the committed output differ on every build.
cpSync(resolve(here, 'src', 'editor.html'), resolve(outDir, 'editor.html'))
cpSync(resolve(here, 'src', 'editor.css'), resolve(outDir, 'editor.css'))
cpSync(resolve(here, 'src', 'preview.html'), resolve(outDir, 'preview.html'))
console.log('Editor bundled to', outDir)
