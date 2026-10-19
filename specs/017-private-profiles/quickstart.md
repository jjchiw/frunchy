# Quickstart: Private Profiles

## Tests

```bash
dotnet test --filter-class "*ProfileRulesTests*"
dotnet test --filter-class "*PrivateProfileTests*"
dotnet test --filter-class "*ProfileVisibilityTests*"
dotnet test --filter-class "*ShareLinkTests*"
dotnet test --filter-class "*ProfileLinksTests*"
dotnet test                                     # the whole suite
```

## By hand, under the AppHost (`bella sdk run -- aspire run`, site on http://localhost:5300)

1. **Private by default.** Sign in as a user with history and open `/u/<you>`: the page shows that only you can see
   it. In a private window, the same address answers "user not found", as does `/u/nobody-at-all`.
2. **Share.** On `/account#visibility`, create a share link and copy it. In the private window, open it: you land
   on the dashboard, and DNA, a year and an anime page open too.
3. **Replace.** Create a new link: the private window's next page load says "user not found". The new link works.
4. **Public.** Choose Public: the private window sees the profile without a link. Choose Private again: hidden.
5. **Comments.** Comment on an episode as the private user. In the private window (without the grant) the name
   isn't a link.
6. **Instance default.** Set `Frunchy__Profiles__DefaultVisibility=public` on the frunchy resource and restart:
   users who never chose are public, and those who chose keep their choice. `user-list` shows the effective
   visibility.

## Validation log

(filled in at the end of implementation)
