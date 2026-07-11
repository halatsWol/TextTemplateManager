import * as esbuild from 'esbuild'
import { cpSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
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

// Cache-bust the asset URLs so WebView2 doesn't serve a stale bundle/css after a rebuild.
const version = Date.now()
let html = readFileSync(resolve(here, 'src', 'editor.html'), 'utf8')
html = html
  .replace('editor.css', `editor.css?v=${version}`)
  .replace('editor.bundle.js', `editor.bundle.js?v=${version}`)
writeFileSync(resolve(outDir, 'editor.html'), html)

cpSync(resolve(here, 'src', 'editor.css'), resolve(outDir, 'editor.css'))
cpSync(resolve(here, 'src', 'preview.html'), resolve(outDir, 'preview.html'))
console.log('Editor bundled to', outDir, 'v' + version)
