### Changed

- Better MyAnimeList matching. A title that differs only by spaces now matches ("The Casebook of Arne" and "The Case
  Book of Arne"), an exact title still wins over a looser one. When a show's title finds nothing on MyAnimeList, its
  longest words are searched on their own (up to three more searches), and every result still has to pass the same
  title rules. A search that found nothing is asked again after 3 days instead of 30, and shows that are new
  (aired in the last 120 days) and still unmatched are looked up again every 3 days. A new operator command,
  `mal-retry-unmatched`, queues every automatic no-match once.

### Upgrade

- After deploying, run `mal-retry-unmatched` once so the shows that were unmatched before are looked up with the new
  rules (manual and ambiguous matches are left alone).
