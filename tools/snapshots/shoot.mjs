// Screenshots the HTML pages PageSnapshots saved, over the Chrome DevTools protocol. No npm packages.
// Run through shoot.sh (it starts Chrome): node shoot.mjs <snapshot dir> <debugging port>
import { readdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { Tab, enable, load, setViewport, sleep, watch } from './cdp.mjs'

const [dir, port] = process.argv.slice(2)
if (!dir || !port) {
  console.error('usage: node shoot.mjs <snapshot dir> <debugging port>')
  process.exit(2)
}

const WIDTHS = [
  { width: 1280, scale: 1, mobile: false, viewportHeight: 800 },
  { width: 360, scale: 2, mobile: true, viewportHeight: 780 },
]

async function fullPage(tab, w, file) {
  const m = await tab.send('Page.getLayoutMetrics')
  const height = Math.ceil(m.cssContentSize.height)
  const { data } = await tab.send('Page.captureScreenshot', {
    format: 'png',
    captureBeyondViewport: true,
    clip: { x: 0, y: 0, width: w.width, height, scale: 1 },
  })
  writeFileSync(file, Buffer.from(data, 'base64'))
}

async function viewportShot(tab, file) {
  const { data } = await tab.send('Page.captureScreenshot', { format: 'png' })
  writeFileSync(file, Buffer.from(data, 'base64'))
}

/** A real mouse click at the centre of the first element matching `selector` */
async function click(tab, selector) {
  const rect = await tab.eval(`(() => {
    const el = document.querySelector(${JSON.stringify(selector)})
    if (!el) return null
    el.scrollIntoView({ block: 'center' })
    const r = el.getBoundingClientRect()
    return { x: r.left + r.width / 2, y: r.top + r.height / 2 }
  })()`)
  if (!rect) throw new Error(`no element for ${selector}`)
  const base = { x: rect.x, y: rect.y, button: 'left', clickCount: 1 }
  await tab.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: rect.x, y: rect.y })
  await tab.send('Input.dispatchMouseEvent', { type: 'mousePressed', ...base })
  await tab.send('Input.dispatchMouseEvent', { type: 'mouseReleased', ...base })
}

const dialogOpen = tab => tab.eval(`document.querySelector('dialog[data-invite-only-dialog]')?.open ?? null`)

async function overflow(tab) {
  return tab.eval(`(() => {
    const vw = window.innerWidth
    const doc = document.documentElement
    const wide = doc.scrollWidth > vw
    const offenders = wide
      ? [...document.querySelectorAll('body *')]
          .filter(e => e.getBoundingClientRect().right > vw + 1)
          .slice(0, 3)
          .map(e => e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\\s+/).slice(0, 3).join('.') : ''))
      : []
    return { wide, scrollWidth: doc.scrollWidth, innerWidth: vw, offenders }
  })()`)
}

const files = readdirSync(join(dir, 'html')).filter(f => f.endsWith('.html')).sort()
const report = []
const signinResults = []
const produced = []

const tab = await Tab.open(port)
const state = { errors: [], navigations: [] }
watch(tab, state)
await enable(tab)

for (const file of files) {
  const [, name, lang] = file.match(/^(.*)-(en|es)\.html$/)
  const url = pathToFileURL(join(dir, 'html', file)).href

  for (const w of WIDTHS) {
    state.errors = []
    state.navigations = []
    await setViewport(tab, w, w.viewportHeight)
    await load(tab, url)

    const o = await overflow(tab)
    const png = join(dir, `${name}-${w.width}-${lang}.png`)
    await fullPage(tab, w, png)
    produced.push(png)
    report.push({ page: `${name}-${w.width}-${lang}`, overflow: w.width === 360 ? o : null, errors: [...new Set(state.errors)] })

    if (name === 'signin') {
      const link = 'a[data-invite-dialog]'

      // 1. first visit: the click must open the dialog
      await setViewport(tab, w, w.viewportHeight)
      await tab.eval(`localStorage.removeItem('frunchy.inviteNoticeSeen')`)
      await load(tab)
      state.navigations = []
      await click(tab, link)
      await sleep(600)
      const opened = await dialogOpen(tab)
      await viewportShot(tab, join(dir, `signin-dialog-${w.width}-${lang}.png`))
      produced.push(join(dir, `signin-dialog-${w.width}-${lang}.png`))
      signinResults.push({ case: `dialog ${w.width} ${lang}`, open: opened, navigated: state.navigations.length > 0, pass: opened === true })

      // 2. notice already seen: the click must leave the dialog shut and follow the link
      await tab.eval(`localStorage.setItem('frunchy.inviteNoticeSeen', '1')`)
      await load(tab)
      state.navigations = []
      await click(tab, link)
      await sleep(600)
      const openedSeen = await dialogOpen(tab)
      await viewportShot(tab, join(dir, `signin-seen-${w.width}-${lang}.png`))
      produced.push(join(dir, `signin-seen-${w.width}-${lang}.png`))
      signinResults.push({
        case: `seen ${w.width} ${lang}`,
        open: openedSeen,
        navigated: state.navigations.length > 0,
        navigatedTo: state.navigations[0] ?? null,
        pass: openedSeen === false,
      })
      await tab.eval(`localStorage.removeItem('frunchy.inviteNoticeSeen')`)
    }
  }
}

tab.close()

console.log(`\n${produced.length} screenshots in ${dir}`)
console.log('\nsign-in dialog checks')
for (const r of signinResults) {
  console.log(
    `  ${r.pass ? 'PASS' : 'FAIL'}  ${r.case}: dialog.open=${r.open}, navigation attempted=${r.navigated}${r.navigatedTo ? ` (${r.navigatedTo})` : ''}`,
  )
}

console.log('\nhorizontal overflow at 360')
const wide = report.filter(r => r.overflow?.wide)
if (wide.length === 0) console.log('  none')
for (const r of wide) console.log(`  ${r.page}: scrollWidth ${r.overflow.scrollWidth} > innerWidth ${r.overflow.innerWidth}; ${r.overflow.offenders.join(', ')}`)

console.log('\nconsole errors')
const bad = report.filter(r => r.errors.length)
if (bad.length === 0) console.log('  none')
for (const r of bad) for (const e of r.errors) console.log(`  ${r.page}: ${e}`)

process.exit(signinResults.every(r => r.pass) ? 0 : 1)
