// The Chrome DevTools protocol pieces shoot.mjs and guide.mjs share. No npm packages.

export const sleep = ms => new Promise(r => setTimeout(r, ms))

/** Root-relative URLs in the saved pages point here; every request to it is aborted (PageSnapshots.fs) */
export const BLOCKED_HOST = 'snapshot.invalid'

/** One DevTools session on one tab */
export class Tab {
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

  static async open(port) {
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

/** Per-page state the event listeners fill in: console errors, and navigations to the blocked host */
export function watch(tab, state) {
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

/** Page, Runtime and Log events on, and the placeholder host blocked */
export async function enable(tab) {
  await tab.send('Page.enable')
  await tab.send('Runtime.enable')
  await tab.send('Log.enable')
  await tab.send('Fetch.enable', { patterns: [{ urlPattern: `http://${BLOCKED_HOST}/*` }] })
}

export async function setViewport(tab, w, height) {
  await tab.send('Emulation.setDeviceMetricsOverride', {
    width: w.width,
    height,
    deviceScaleFactor: w.scale,
    mobile: w.mobile,
  })
}

/** Navigate (or reload) and wait until Tailwind's CDN build has styled the page and Alpine has started */
export async function load(tab, url) {
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
