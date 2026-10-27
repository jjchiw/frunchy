### Added

- The fediverse on the Caravana deploy. `aspire deploy` now publishes Megáfono, its gateway and its own
  Postgres next to Frunchy and its Postgres, with federation on, so people on Mastodon and other servers
  can follow a Public profile and an anime, and see and answer comments on episodes. The gateway takes
  over the domain: it sends Megáfono's routes to Megáfono and everything else to Frunchy. **The domain
  becomes permanent once someone has followed**: changing it breaks every follow. `Deploy:Federation=false`
  keeps the deploy as it was. See "Upgrade" below and `docs/deploy-caravana.md`.

### Changed

- Megáfono is pinned at 0.2.0 (was 0.1.0) in the AppHost and in `deploy/compose.yaml`
  (`MEGAFONO_VERSION`). With federation on, Members and Private profiles are now not discoverable and
  not indexable on the fediverse, as Frunchy already asked: 0.1.0 marked every user as discoverable. A
  self-hosted Megáfono 0.1.0 database is upgraded by Megáfono itself on start.

### Upgrade

- Before releasing a deploy with federation (the default now), add four secrets to Bella
  `frunchy/production` as `Parameters__<name>` keys: `megafono-master-key` (`openssl rand -base64 32`),
  and `megafono-token`, `megafono-webhook-secret` and `megafono-db-password` (each `openssl rand -hex 32`).
  The release stops, naming the parameter, if the master key is missing or is not base64 of 32 bytes.
- Back up the master key apart from the database: it encrypts every actor's private key in Megáfono's
  database, and Caravana's backups do not include secrets.
- Check the Caravana host's memory first: the app now reserves about 960 MB (up to about 2.2 GB at its
  ceilings), up from 512 MB.
- The domain is permanent from the first follow: never change `frunchy-domain`, never wipe Megáfono's
  database, and never run a test instance on the production domain.
- To stay without federation, set `Deploy__Federation=false` for the deploy: the app is `frunchy` and `pg`
  again, with no new secrets needed.
