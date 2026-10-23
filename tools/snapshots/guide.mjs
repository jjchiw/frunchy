// The /guide screenshots of Frunchy's own pages (the extension's come from its own repository), over the Chrome
// DevTools protocol. Run through guide.sh (it starts Chrome): node guide.mjs <snapshot dir> <debugging port> <output dir>
import { existsSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { Tab, enable, load, setViewport, watch } from './cdp.mjs'

const [dir, port, out] = process.argv.slice(2)
if (!dir || !port || !out) {
  console.error('usage: node guide.mjs <snapshot dir> <debugging port> <output dir>')
  process.exit(2)
}

// Desktop width, at 2x so the text stays sharp when the guide scales the image down
const VIEWPORT = { width: 1280, scale: 2, mobile: false }
const VIEWPORT_HEIGHT = 800
// Room around a cropped element, in CSS pixels
const PAD = 24

// Invented values typed into the sign-up form; the invite has the real shape (InviteCode.generate) but no row
const fillSignup = `(() => {
  const set = (name, value) => {
    const el = document.querySelector('input[name="' + name + '"]')
    if (!el) return
    el.value = value
    el.dispatchEvent(new Event('input', { bubbles: true }))
  }
  set('invite', 'inv_7KQ2M9XC4TBR')
  set('username', 'mika')
  set('displayName', 'Mika')
})()`

// The test server's origin shows in a few texts (the sign-up form's address preview): the placeholder site the
// extension's shots use instead, after Alpine has rendered; and broken pictures hidden
const siteOrigin = `(async () => {
  await new Promise(r => setTimeout(r, 300))
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT)
  while (walker.nextNode()) {
    const n = walker.currentNode
    if (n.nodeValue.includes('http://localhost')) n.nodeValue = n.nodeValue.replaceAll('http://localhost', 'https://frunchy.example')
  }
  // a picture that didn't load (the scripted MyAnimeList catalog's posters have no real address) would show its alt
  // text: leave its frame empty instead
  for (const img of document.images) if (img.complete && img.naturalWidth === 0) img.style.visibility = 'hidden'
})()`

/**
 * One screenshot per entry and language: `page` is the PageSnapshots name, `target` an expression giving the element to
 * crop (none: the top of the page at the viewport's size), `prepare` an expression run before the shot.
 */
const SHOTS = [
  { name: 'signup', page: 'signup', target: `document.querySelector('#main .fr-card')`, prepare: fillSignup },
  { name: 'account-extension', page: 'account', target: `document.querySelector('[data-account="key-active"]')` },
  { name: 'sync-key', page: 'sync-key', target: `document.querySelector('[data-new-sync-key]').closest('.fr-card')` },
  { name: 'dashboard', page: 'dashboard' },
  { name: 'dna', page: 'dna' },
  { name: 'account-visibility', page: 'account-shared', target: `document.querySelector('[data-account="visibility"]')` },
]

async function save(tab, file, clip) {
  const params = clip
    ? { format: 'png', captureBeyondViewport: true, clip: { ...clip, scale: 1 } }
    : { format: 'png' }
  const { data } = await tab.send('Page.captureScreenshot', params)
  writeFileSync(file, Buffer.from(data, 'base64'))
}

/** The element's box in page coordinates, padded and kept inside the page */
async function boxOf(tab, target) {
  return tab.eval(`(() => {
    const el = ${target}
    if (!el) return null
    const r = el.getBoundingClientRect()
    const pad = ${PAD}
    const x = Math.max(0, r.left + scrollX - pad)
    const y = Math.max(0, r.top + scrollY - pad)
    const width = Math.min(document.documentElement.scrollWidth - x, r.width + 2 * pad)
    return { x, y, width, height: r.height + 2 * pad }
  })()`)
}

const tab = await Tab.open(port)
const state = { errors: [], navigations: [] }
watch(tab, state)
await enable(tab)

const produced = []
const problems = []

for (const lang of ['en', 'es']) {
  for (const shot of SHOTS) {
    const html = join(dir, 'html', `${shot.page}-${lang}.html`)
    if (!existsSync(html)) {
      problems.push(`${shot.name}-${lang}: no ${html} (run PageSnapshots first)`)
      continue
    }

    state.errors = []
    await setViewport(tab, VIEWPORT, VIEWPORT_HEIGHT)
    await load(tab, pathToFileURL(html).href)
    if (shot.prepare) await tab.eval(shot.prepare)
    await tab.eval(siteOrigin)

    let clip = null
    if (shot.target) {
      clip = await boxOf(tab, shot.target)
      if (!clip) {
        problems.push(`${shot.name}-${lang}: nothing matches ${shot.target}`)
        continue
      }
    }

    const file = join(out, `${shot.name}-${lang}.png`)
    await save(tab, file, clip)
    produced.push(file)
    for (const e of new Set(state.errors)) problems.push(`${shot.name}-${lang}: ${e}`)
  }
}

tab.close()

console.log(`\n${produced.length} guide screenshots in ${out}`)
for (const f of produced) console.log(`  ${f}`)
console.log('\nproblems')
if (problems.length === 0) console.log('  none')
for (const p of problems) console.log(`  ${p}`)

process.exit(produced.length === SHOTS.length * 2 ? 0 : 1)
