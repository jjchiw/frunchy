### Added

- Operators can follow the same series on other Frunchy instances. On `/review/follows` (only with federation
  on) typing a server name such as `otherfrunchy.io` follows, on that instance, every series people watch
  here, and typing `@frieren@otherfrunchy.io` follows one series. Their episode threads then show on our
  episode pages as "Also discussed on otherfrunchy.io", with a link there. Only Frunchy instances can be
  followed (Frunchy checks the server's NodeInfo), and nothing about anyone's watch history is sent: only which
  series are watched here. `fediverse-status` now counts the follows.
- Comments written in another instance's thread show on our episode page, marked "via otherfrunchy.io", once
  Megáfono passes on the replies a followed series account announces (not in Megáfono 0.2.0).

### Changed

- The threads Frunchy publishes now carry the series and episode ids, so another Frunchy instance can match them
  to its own episode.

### Upgrade

- Megáfono must advertise `frunchy` in NodeInfo so other instances recognise yours. The AppHost and
  `deploy/compose.yaml` set `Megafono__Software__Name=frunchy`; if you run Megáfono with your own compose file
  or environment, set `Megafono__Software__Name=frunchy` yourself. Until then other instances refuse to follow you.
- `Frunchy:Federation:AllowPrivateAddresses` (default false) is for a local development fediverse only; Frunchy
  refuses to start with it set to true in Production.
