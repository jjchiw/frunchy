# Research: Taste Compatibility

## R1. Where the code lives

- **`Infrastructure/Compatibility.fs` (new)**: shared by two slices, the dashboard card and the
  compare page, the way `MalActions` is shared. It has:
  - `load (db: DbConfig) (viewer: Guid) (owner: Guid) : Task<Comparison>`: two SQL statements, F#
    for the rest;
  - the pure functions `score`, `level`, `cosine`, `sharedGenres` and so on, unit-tested.
- **`Handlers/Users/Dashboard.fs`**: renders the card when `WebAuth.currentUserId ctx` is set and
  isn't the owner. The card view lives in this slice, its only user.
- **`Handlers/Users/Compare.fs` (new)**: `GET /u/{username}/compare`.
- **Files and order**: `Compatibility.fs` goes after the other `Infrastructure` modules in the
  fsproj, and `Compare.fs` after `Anime.fs`.

## R2. The data, per user (statement 1)

One statement takes both user ids (`= ANY(@users)`) and returns tagged rows, as the DNA page does:

| Kind | Per user | Rule |
|---|---|---|
| `totals` | fully watched episodes (for the 15 threshold), series count | the dashboard's rules: episodes with a known length |
| `series` | series id, title, minutes, fully watched episodes | minutes count a partial episode up to its playhead (the dashboard's rule) |
| `genre` | genre name, deduplicated minutes | the DNA page's `dna` CTE (a dub and its original once, matched seasons only) |
| `studio` | studio name, deduplicated minutes, the series ids it covers | the same, `kind = 'studio'` |
| `coverage` | matched deduplicated minutes, all deduplicated minutes | the DNA coverage note |

Titles follow the DNA page's rule: `cr_series.title`, else the episodes' series title.

## R3. The score (pure, `Compatibility.score`)

- **Shared series**: `S = |A ∩ B| / min(|A|, |B|)`, or 0 when either side is empty.
- **Genre similarity**: `G = cosine(a, b)` over the union of genres, where `a` and `b` are each
  person's minutes per genre. When either vector is empty, G is undefined.
- **Score**: `(S + G) / 2`, or `S` alone when G is undefined.
- **Level**: Low below 0.2, Some below 0.4, Good below 0.6, High below 0.8, Very high from 0.8.
- **Symmetry**: both signals are symmetric, so the level is the same from either side (SC-002).
  A property test checks it on random vectors.
- **Floating point**: computed in `float`; the bands use `<` on the exact thresholds. A unit test
  checks each boundary.

## R4. What the sections need

- **Shared genres (card)**: genres in both people's top 5 by share, ordered by the sum of shares,
  up to 3.
- **Genre comparison (page)**: the union of each person's top 4 by share, ordered by the sum of
  shares, up to 6. Each genre shows its share of that person's genre minutes (the DNA page's rule,
  so the figures match the DNA page), with the coverage note: matched minutes ÷ all deduplicated minutes, per person (the DNA wording).
- **Shared series**: ordered by the smaller of the two people's minutes (descending), then title.
  The card shows 4; the page shows 10 plus "and N more".
- **Their series, not yours**: the owner's series the viewer never watched, ordered by the owner's
  minutes, up to 8. **Statement 2** adds, for those ids only, the MyAnimeList score and up to 3
  genres of the series' most-watched matched season (the anime page's poster rule).
- **Shared studios**: studios in both people's studio rows. They're ordered by the number of
  shared series they cover, then by summed minutes. The card shows the first, the page 5. "Shared
  series it covers" counts series in the shared set whose matched seasons list that studio, on both
  sides.
- **Summary sentence**: the shared count, plus the first shared genre when there is one.

## R5. Access and errors

The compare page's handler checks, in this order:
1. `WebAuth.requireSignedIn`, which sends a signed-out visitor to sign-in and back to
   `/u/{username}/compare`.
2. `Username.tryCreate`; an invalid username gets the not-found page.
3. A user lookup; an unknown user gets the not-found page.
4. Self, which redirects to `/u/{username}`.
5. `Compatibility.load`.

On the dashboard, the card is only computed for a signed-in viewer who isn't the owner. A
dashboard with a signed-out viewer runs no extra query.

## R6. Performance (SC-003)

Statement 1 scans each user's `watch_events` (PK starts with `user_id`) with the same joins as the
DNA page, which renders in well under a second. Statement 2 touches at most 8 series. A test seeds
two users with 2,000 synthetic watched episodes each (`Fixtures.syntheticPages`) and times the
page after a warm-up, against 1 s.

## R7. Look

- **Design system**: Night Broadcast, through `impeccable` shape → build → audit, with the pitch
  layout from https://claude.ai/artifact/D8QVV7NJaTvGogvBJBB2yV (A and B).
- **Colours**: the viewer's bars and figures are amber (`primary`), the other person's rose
  (`secondary`), and genres and studios lilac pills (`tertiary`).
- **Genre bars**: solid fills, and the two people sit on two rows per genre (works at 360px). The
  pitch's mirrored desktop layout is allowed from `md` up if it stays readable.
- **Meter**: 5 segments, as `role="img"` with `aria-label` "{level}: {n} of 5".
- **Shared components**: a new `Components.levelMeter`. `shareBar` takes a colour role. Each is
  added only if both the card and the page use it (constitution III).
