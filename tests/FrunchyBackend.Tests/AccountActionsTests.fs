module FrunchyBackend.Tests.AccountActionsTests

open Xunit
open FrunchyBackend.Operations
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private events (db) =
    Db.dump db "SELECT e.event, coalesce(e.provider,'-'), e.actor FROM security_events e ORDER BY e.id"

[<Fact>]
let ``operator key regeneration and binding reset are recorded with actor operator`` () =
    task {
        use! app = newApp ()
        let! _ = createUser app.Db "animenerd"
        let! _ = RegenerateKey.regenerateKey app.Db "animenerd"
        let! _ = ResetBinding.resetBinding app.Db "animenerd"
        let! rows = events app.Db
        Assert.Equal<string list>([ "sync_key_generated|-|operator"; "binding_reset|-|operator" ], rows)
    }

[<Theory>]
[<InlineData("admin")>]
[<InlineData("Account")>]
[<InlineData("signin")>]
[<InlineData("frunchy")>]
let ``reserved usernames are refused by user-create`` (name: string) =
    task {
        let! db = newDatabase ()
        let! code = CreateUser.run db [ name; "X" ]
        Assert.Equal(3, code)
    }

[<Fact>]
let ``deleting a user removes their security events`` () =
    task {
        use! app = newApp ()
        let! _ = createUser app.Db "animenerd"
        let! _ = RegenerateKey.regenerateKey app.Db "animenerd"
        let! _ = DeleteUser.deleteUser app.Db "animenerd"
        let! remaining = Db.count app.Db "security_events"
        Assert.Equal(0L, remaining)
    }

[<Theory>]
[<InlineData("en", "Sign in")>]
[<InlineData("es", "Iniciar sesión")>]
let ``signed-out navbar offers sign in on home and dashboards`` (language: string, expected: string) =
    task {
        use! app = newApp ()
        let! _ = createUser app.Db "animenerd"
        use browser = app.NewBrowser language
        let! home = browser.Get "/"
        let! dashboard = browser.Get "/u/animenerd"
        Assert.Contains($">{expected}<", home.Html)
        Assert.Contains($">{expected}<", dashboard.Html)
        Assert.Contains("href=\"/signin\"", home.Html)
    }
