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

// editor.html is deliberately NOT copied here: the .csproj takes it straight from src/ (it needs no
// build step, and MainPage inlines the css/bundle into it anyway, so the old ?v= cache-buster was
// stripped before WebView2 ever saw it). src/editor.html is the only copy.
cpSync(resolve(here, 'src', 'editor.css'), resolve(outDir, 'editor.css'))
cpSync(resolve(here, 'src', 'preview.html'), resolve(outDir, 'preview.html'))
console.log('Editor bundled to', outDir)
