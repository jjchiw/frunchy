# Design brief: Progress (feature 031)

For the frunchy_designer session. Night Broadcast applies (design/night_broadcast/DESIGN.md): amber is the person the
page is about, lilac tags, solid fills, plain copy, no eyebrow labels, no emoji. Tests read only the `data-*` hooks in
`contracts/hooks.md`; layout, components and copy tone are yours.

## Why

The owner forgets shows they started: "on MyAnimeList there are 12 episodes but I only watched 4". A fourth profile tab,
**Progress** (`/u/{username}/progress`), after Dashboard, Anime DNA, Year in review.

## What it shows

- **Three counts** for everyone who can see the profile: Completed, Watching, Unfinished (a show = a MyAnimeList
  entry, an anime season). Plus a quiet note when some started shows have no MyAnimeList details (not counted).
- **For the owner only**, lists:
  - **Unfinished** (the point of the page): each show with poster, title (links to the public series page), "4 of
    12 episodes" (or "4 episodes" while the total is unknown), "last watched 3 months ago", and **"Next: episode 5"**
    linking to the episode (or to Crunchyroll). Newest first. A real history can have 50-150 of these.
  - **Watching**: same shape, usually a handful (watched in the last 30 days, or an airing show kept up with).
  - **Completed**: the 12 most recently completed, with the count of all.
- **Long runners** (One Piece, 1,000+ episodes) can be Watching or Unfinished: never a progress bar that reads
  266 of 1,155 as failure.
- **Visitors** (not the owner): the counts and a short line ("The list of shows is only shown to {name}" or similar),
  never a title.

## States to design

Desktop 1280 and phone 360, English and Spanish (Spanish ~25% longer: "Sin terminar", "Siguiente: episodio 5"):
- owner with many unfinished (100+), a few watching, many completed;
- owner with nothing unfinished (all caught up: a positive state, not empty);
- owner with nothing started (empty state);
- visitor view;
- an unfinished long runner; an airing show with no total; a show without a poster.

## Questions for you

1. How do 100+ unfinished shows stay usable (cap + "Show all", group by year of last watch, a compact row instead of
   tiles)?
2. Do the three counts read as stat tiles (like the dashboard), or one line with a proportional bar?
3. How does the "Next: episode 5" action look: a link, a pill, a button?
4. Should Unfinished come before Watching (it's the reason for the page) or follow the natural order?

## Process

The first build uses existing components (profileTop, figure tiles, poster, seriesLink, pills, `<details>`). The owner's
session sends screenshots (PageSnapshots, 1280 and 360, en and es, owner and visitor) for your review.
