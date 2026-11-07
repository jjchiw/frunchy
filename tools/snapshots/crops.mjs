// Close crops for the theme review (feature 032): the footer in Spanish at 360, and the header mark, in both themes.
// Run through chrome.sh: bash tools/snapshots/chrome.sh <snapshot dir> crops.mjs   (needs html/home-es.html)
// Writes <dir>/footer-360-es[-light].png and <dir>/mark-header-1280-en[-light].png (the mark at 6x).
import { writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { Tab, enable, load, setColorScheme, setViewport } from './cdp.mjs'

const [dir, port] = process.argv.slice(2)
const tab = await Tab.open(port)
await enable(tab)

async function crop(selector, file, scale) {
  const box = await tab.eval(`(() => {
    const r = document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect()
    return { x: r.left + scrollX, y: r.top + scrollY, width: r.width, height: r.height }
  })()`)
  const pad = 8
  const clip = { x: Math.max(0, box.x - pad), y: Math.max(0, box.y - pad), width: box.width + 2 * pad, height: box.height + 2 * pad, scale }
  const { data } = await tab.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true, clip })
  writeFileSync(file, Buffer.from(data, 'base64'))
  console.log(file)
}

for (const { scheme, suffix } of [{ scheme: 'dark', suffix: '' }, { scheme: 'light', suffix: '-light' }]) {
  await setColorScheme(tab, scheme)

  await setViewport(tab, { width: 360, scale: 2, mobile: true }, 780)
  await load(tab, pathToFileURL(join(dir, 'html', 'home-es.html')).href)
  await crop('footer', join(dir, `footer-360-es${suffix}.png`), 2)

  await setViewport(tab, { width: 1280, scale: 1, mobile: false }, 800)
  await load(tab, pathToFileURL(join(dir, 'html', 'home-en.html')).href)
  await crop('header a svg', join(dir, `mark-header-1280-en${suffix}.png`), 6)
}

tab.close()
