# Design brief: Seasons (feature 027)

For the frunchy_designer session. Frunchy's "Night Broadcast" system applies (design/night_broadcast/DESIGN.md):
amber is the person the page is about, lilac is tags, solid fills, plain copy, no eyebrow labels, no emoji icons.
Tests read only the `data-*` hooks in `contracts/hooks.md`; the layout, order within a card, sizes and copy tone
are yours.

## Why

On the Anime DNA, One Piece (266 episodes, 106 hours) is the flagship, Toei is the favourite studio and the 1990s
show up because One Piece started in 1999. Long shows swallow every figure. Feature 027:

1. Taste figures (studio, genres, decades, formats) count **watched seasons** instead of minutes. Copy changes
   from "7.3%" + "Based on 95.4% of the watch time" to shares of seasons + "Based on 41 of 45 seasons".
2. The flagship ("Anime insignia") skips long runners (more than 100 episodes).
3. A new **long-run card**: the top long runner, when the person fully watched 24+ of its episodes: title, poster,
   episodes fully watched, hours, and the airing seasons it spans ("Fall 1999 to Fall 2026").
4. A new **"Your seasons"** section: by airing season (Winter, Spring, Summer, Fall YYYY, from each episode's air
   date): the top season (most shows), then the 8 newest seasons, newest first, each with its shows (poster, title,
   episodes fully watched, and a "watched while airing" mark for shows with an episode fully watched within 7 days
   of airing), and a line for the earlier seasons not listed ("3 earlier seasons").

## Pages and states to design

- DNA, desktop 1280 and phone 360, English and Spanish (Spanish runs ~25% longer: "Otoño 2026", "Visto en emisión").
- Long-run card: present (One Piece), absent. Next to or under the flagship: your call; both cards must read as
  different things (the flagship is "your show", the long run is "your marathon").
- Flagship absent (every series a long runner) with the long-run card present.
- "Your seasons": 1 season; 8+ seasons with the "earlier seasons" line; a season with 1 show; a season with 15 shows
  (One Piece fans who also watch seasonals: think about how many posters a season shows before "and N more").
  A show watched while airing vs not.
- The studio/genre/decade cards with the new "seasons" wording.

## Questions for you

- Where does "Your seasons" sit on the DNA page (it's long; a season grid, a vertical timeline, a horizontal strip)?
- How is "watched while airing" shown (a small tag, an icon from `Theme.Icon`, an amber dot)?
- Should the top season be its own highlight card or the first, emphasised row?

## Process

The first build uses existing components (figureSection, poster, seriesLink, chips). The owner's session sends
screenshots from `tools/snapshots` (PageSnapshots) at 1280 and 360, en and es; you review against this brief.
