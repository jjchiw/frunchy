# Design pitches

Two Claude Design canvases from October 2026, kept here because the canvases themselves are gone.
The originals were `https://claude.ai/artifact/Jzm6navcFrNnQ8TSNwcESK` (visual directions) and
`https://claude.ai/artifact/D8QVV7NJaTvGogvBJBB2yV` (taste compatibility); those links no longer work.

The files are the canvases' own sources: `canvas.json` is the board layout, and each `*.dc.html` is one
artboard, in Claude Design's component format. They render inside Claude Design, not on their own: the
`support.js` they load is the canvas runtime, and the `{{…}}` holes and `<sc-for>` loops are filled by it.
Opened directly in a browser, they show their static markup only.

To see them again, publish this folder's files to a new Claude Design canvas (Artifact tool, the Design type),
keeping each file's name and `canvas.json` as the index. The shipped site is the reference for what was
built; these show what was pitched.

## visual-directions/ (feature 010)

Three directions for the whole site, each with an identity sheet, the dashboard and the episode page. Row 2,
**Night Broadcast**, was chosen and built in feature 010 (`design/night_broadcast/DESIGN.md`).

| Direction | Identity | Dashboard | Episode page |
|---|---|---|---|
| 1. Screentone (light, printed manga ink) | `Main.dc.html` | `ScreentoneDashboard.dc.html` | `ScreentoneEpisode.dc.html` |
| 2. Night Broadcast (warm, late-night TV), **chosen** | `BroadcastIdentity.dc.html` | `BroadcastDashboard.dc.html` | `BroadcastEpisode.dc.html` |
| 3. Neo-Scrobble, sharpened (the pre-010 look) | `ScrobbleIdentity.dc.html` | `ScrobbleDashboard.dc.html` | `ScrobbleEpisode.dc.html` |

Not built from the pitch: the "ON AIR" and "NOW SHOWING" labels (eyebrows, and untrue) and hours as `486:12`
(spec 010, Clarifications).

## taste-compatibility/ (feature 011)

Drawn in the pre-010 palette; the layout carried over into Night Broadcast.

| Board | What |
|---|---|
| `Main.dc.html` | A. The card on another person's profile (**built**) |
| `Compare.dc.html` | B. The side-by-side compare page (**built**, with C's heading) |
| `Overlap.dc.html` | C. Only you, both, only them (its headline sentence was used) |
| `CompareMobile.dc.html` | B at 390px |

The pitches show a percentage-free level and sample figures; the built feature computes everything from real
histories (spec 011).
