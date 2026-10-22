// Screenshots the HTML pages PageSnapshots saved, over the Chrome DevTools protocol. No npm packages.
// Run through shoot.sh (it starts Chrome): node shoot.mjs <snapshot dir> <debugging port>
import { readdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

const [dir, port] = process.argv.slice(2)
if (!dir || !port) {
  console.error('usage: node shoot.mjs <snapshot dir> <debugging port>')
  process.exit(2)
}

const sleep = ms => new Promise(r => setTimeout(r, ms))
const WIDTHS = [
  { width: 1280, scale: 1, mobile: false, viewportHeight: 800 },
  { width: 360, scale: 2, mobile: true, viewportHeight: 780 },
]
const BLOCKED_HOST = 'snapshot.invalid'

/** One DevTools session on one tab */
class Tab {
  constructor(ws) {
    this.ws = ws
    this.nextId = 1
    this.pending = new Map()
    this.listeners = []
    ws.addEventListener('message', event => {
      const msg = JSON.parse(event.data)
      if (msg.id && this.pending.has(msg.id)) {
        const { resolve, reject } = this.pending.get(msg.id)
        this.pending.delete(msg.id)
        msg.error ? reject(new Error(`${msg.error.message}`)) : resolve(msg.result)
      } else if (msg.method) {
        for (const l of this.listeners) l(msg.method, msg.params)
      }
    })
  }

  static async open() {
    const res = await fetch(`http://127.0.0.1:${port}/json/new?about:blank`, { method: 'PUT' })
    const info = await res.json()
    const ws = new WebSocket(info.webSocketDebuggerUrl)
    await new Promise((resolve, reject) => {
      ws.addEventListener('open', resolve)
      ws.addEventListener('error', reject)
    })
    return new Tab(ws)
  }

  send(method, params = {}) {
    const id = this.nextId++
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject })
      this.ws.send(JSON.stringify({ id, method, params }))
    })
  }

  on(fn) {
    this.listeners.push(fn)
  }

  once(method, timeout = 30000) {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`timeout waiting for ${method}`)), timeout)
      const fn = (m, p) => {
        if (m === method) {
          clearTimeout(timer)
          this.listeners = this.listeners.filter(x => x !== fn)
          resolve(p)
        }
      }
      this.listeners.push(fn)
    })
  }

  async eval(expression) {
    const r = await this.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text)
    return r.result.value
  }

  close() {
    this.ws.close()
  }
}

/** Per-page state the event listeners fill in */
function watch(tab, state) {
  tab.on((method, p) => {
    if (method === 'Runtime.consoleAPICalled' && p.type === 'error') {
      state.errors.push('console.error: ' + p.args.map(a => a.value ?? a.description ?? '').join(' '))
    } else if (method === 'Runtime.exceptionThrown') {
      state.errors.push('exception: ' + (p.exceptionDetails.exception?.description ?? p.exceptionDetails.text))
    } else if (method === 'Log.entryAdded' && p.entry.level === 'error') {
      // the blocked placeholder host is on purpose
      if (!(p.entry.url ?? '').includes(BLOCKED_HOST)) state.errors.push(`log: ${p.entry.text} ${p.entry.url ?? ''}`.trim())
    } else if (method === 'Fetch.requestPaused') {
      // every request to the placeholder host stops here: nothing resolves, and a navigation never leaves the page
      if (p.resourceType === 'Document') state.navigations.push(p.request.url)
      tab.send('Fetch.failRequest', { requestId: p.requestId, errorReason: 'Aborted' }).catch(() => {})
    }
  })
}

async function setViewport(tab, w, height) {
  await tab.send('Emulation.setDeviceMetricsOverride', {
    width: w.width,
    height,
    deviceScaleFactor: w.scale,
    mobile: w.mobile,
  })
}

/** Navigate (or reload) and wait until Tailwind's CDN build has styled the page and Alpine has started */
async function load(tab, url) {
  const loaded = tab.once('Page.loadEventFired')
  if (url) await tab.send('Page.navigate', { url })
  else await tab.send('Page.reload')
  await loaded
  const deadline = Date.now() + 15000
  while (Date.now() < deadline) {
    const ready = await tab.eval(`(() => {
      const bg = getComputedStyle(document.body).backgroundColor
      const styled = bg !== 'rgba(0, 0, 0, 0)' && bg !== 'rgb(255, 255, 255)'
      const needsAlpine = !!document.querySelector('[x-data]')
      const alpine = !needsAlpine || !!document.querySelector('[x-data]')._x_dataStack
      return styled && alpine
    })()`)
    if (ready) break
    await sleep(150)
  }
  await tab.eval('document.fonts.ready.then(() => true)')
  await sleep(1500)
}

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

const tab = await Tab.open()
const state = { errors: [], navigations: [] }
watch(tab, state)
await tab.send('Page.enable')
await tab.send('Runtime.enable')
await tab.send('Log.enable')
await tab.send('Fetch.enable', { patterns: [{ urlPattern: `http://${BLOCKED_HOST}/*` }] })

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
