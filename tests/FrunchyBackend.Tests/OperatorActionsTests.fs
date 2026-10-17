/// The operator commands and the shared actions (feature 008, US3): the terminal does what the root's
/// page does, leaves the same rows and events, and works with no root configured (the way back in).
module FrunchyBackend.Tests.OperatorActionsTests

open System
open System.Net
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend
open FrunchyBackend.Infrastructure
open FrunchyBackend.Operations
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// An app with the accounts boss and newbie; `root` is what `Frunchy:RootOperator` says, if anything
let private newApp (root: string option) =
    task {
        let! db = newDatabase ()
        let config = root |> Option.map (fun r -> [ "Frunchy:RootOperator", r ]) |> Option.defaultValue []
        let! app = createAppWith db config ignore
        let! boss = signedUp app "sub-boss" "boss"
        let! newbie = signedUp app "sub-newbie" "newbie"
        return app, boss, newbie
    }

/// Run a command through the CLI map, as `dotnet run -- <command>` does
let private cli (app: TestApp) (root: string option) (command: string) (args: string list) : Task<int> =
    let env: Program.CliEnv =
        { Db = app.Db
          Http = httpFactory app
          Mal = MalClient.readSettings (ServiceProviderServiceExtensions.GetRequiredService<IConfiguration>(app.Services))
          Clock = TimeProvider.System
          RootOperator = root }

    Program.cliCommands[command] env args

let private webChange (boss: FrunchyBackend.Tests.Infrastructure.Browser.Browser) (action: string) (username: string) =
    task {
        let! response = boss.PostForm("/review/operators", $"/review/operators/{action}", [ "username", username ])
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        return string response.Headers.Location
    }

let private rows (app: TestApp) =
    Db.dump app.Db "SELECT u.username || ' ' || coalesce(g.username, '-') FROM operators o JOIN users u ON u.id = o.user_id LEFT JOIN users g ON g.id = o.added_by ORDER BY u.username"

let private events (app: TestApp) (excluding: string) =
    Db.dump
        app.Db
        $"SELECT u.username || ' ' || e.event || ' ' || e.actor FROM security_events e JOIN users u ON u.id = e.user_id WHERE e.event LIKE 'operator_%%' AND u.username <> '{excluding}' ORDER BY e.id"

[<Fact>]
let ``the terminal and the page leave the same rows and events, except the root's own event row`` () =
    task {
        let root = Some "boss"
        let! web, boss, _ = newApp root
        use web = web
        let! terminal, _, _ = newApp root
        use terminal = terminal

        // add
        let! location = webChange boss "add" "newbie"
        Assert.Equal("/review/operators?msg=added", location)
        let! code = cli terminal root "operator-add" [ "newbie" ]
        Assert.Equal(0, code)

        // the only difference in the table: who is on record as having added it
        let! webRows = rows web
        let! terminalRows = rows terminal
        Assert.Equal<string list>([ "newbie boss" ], webRows)
        Assert.Equal<string list>([ "newbie -" ], terminalRows)

        let! webEvents = events web "boss"
        let! terminalEvents = events terminal "boss"
        Assert.Equal<string list>([ "newbie operator_added operator" ], webEvents)
        Assert.Equal<string list>(webEvents, terminalEvents)

        // remove
        let! location = webChange boss "remove" "newbie"
        Assert.Equal("/review/operators?msg=removed", location)
        let! code = cli terminal root "operator-remove" [ "newbie" ]
        Assert.Equal(0, code)
        let! webRows = rows web
        let! terminalRows = rows terminal
        Assert.Empty webRows
        Assert.Empty terminalRows

        let! webEvents = events web "boss"
        let! terminalEvents = events terminal "boss"
        Assert.Equal<string list>([ "newbie operator_added operator"; "newbie operator_removed operator" ], webEvents)
        Assert.Equal<string list>(webEvents, terminalEvents)

        // the root's own row exists only on the page's side, as the root's own action
        let! rootWeb = Db.dump web.Db "SELECT e.event || ' ' || e.actor FROM security_events e JOIN users u ON u.id = e.user_id WHERE e.event LIKE 'operator_%' AND u.username = 'boss' ORDER BY e.id"
        let! rootTerminal = Db.dump terminal.Db "SELECT e.event FROM security_events e JOIN users u ON u.id = e.user_id WHERE e.event LIKE 'operator_%' AND u.username = 'boss'"
        Assert.Equal<string list>([ "operator_added user"; "operator_removed user" ], rootWeb)
        Assert.Empty rootTerminal
    }

[<Fact>]
let ``operator-add and operator-remove exit codes, and a refusal changes nothing`` () =
    task {
        let root = Some "boss"
        let! app, _, _ = newApp root
        use app = app
        let run command args = cli app root command args

        // usage and bad usernames: 1
        for command in [ "operator-add"; "operator-remove" ] do
            for args in [ []; [ "a"; "b" ]; [ "a b" ]; [ "ab" ]; [ "admin" ]; [ "x'; DROP TABLE operators;--" ] ] do
                let! code = run command args
                Assert.True((code = 1), $"{command} {args} exited {code}")

        // unknown user: 2
        let! code = run "operator-add" [ "nobody" ]
        Assert.Equal(2, code)
        let! code = run "operator-remove" [ "nobody" ]
        Assert.Equal(2, code)

        // the root is configuration only: 4, in any letter case
        for command in [ "operator-add"; "operator-remove" ] do
            for name in [ "boss"; "BOSS"; " Boss " ] do
                let! code = run command [ name ]
                Assert.True((code = 4), $"{command} '{name}' exited {code}")

        // not an operator yet: 3 for remove
        let! code = run "operator-remove" [ "newbie" ]
        Assert.Equal(3, code)

        let! before = Db.dump app.Db "SELECT e::text FROM security_events e ORDER BY id"
        let! none = rows app
        Assert.Empty none
        Assert.DoesNotContain(before, fun e -> e.Contains "operator_")

        // success: 0 (case and spaces are forgiven, as on the page), then already an operator: 3
        let! code = run "operator-add" [ " Newbie " ]
        Assert.Equal(0, code)
        let! code = run "operator-add" [ "newbie" ]
        Assert.Equal(3, code)
        let! rows' = events app "boss"
        Assert.Equal<string list>([ "newbie operator_added operator" ], rows')

        let! code = run "operator-remove" [ "newbie" ]
        Assert.Equal(0, code)
        let! gone = rows app
        Assert.Empty gone
    }

[<Fact>]
let ``operator-list shows the root from configuration and each operator with who added them`` () =
    task {
        let root = Some "boss"
        let! app, boss, _ = newApp root
        use app = app
        let! _ = webChange boss "add" "newbie"
        let! code = cli app root "operator-list" []
        Assert.Equal(0, code)

        let! listed = OperatorActions.list app.Db
        let lines = OperatorList.render root listed
        Assert.Equal(3, lines.Length)
        Assert.Equal("Root operator (from configuration): boss", lines[0])
        Assert.StartsWith("USERNAME", lines[1])
        Assert.StartsWith("newbie", lines[2])
        Assert.Contains("NEWBIE", lines[2])
        Assert.Contains("boss", lines[2])
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", lines[2])

        // added from the terminal: no one on record
        let! _ = cli app root "operator-remove" [ "newbie" ]
        let! _ = cli app root "operator-add" [ "newbie" ]
        let! listed = OperatorActions.list app.Db
        Assert.Contains(" - ", (OperatorList.render root listed)[2])

        // no root configured
        Assert.Equal("Root operator (from configuration): none", (OperatorList.render None [])[0])
        Assert.Equal(2, (OperatorList.render None []).Length)
    }

[<Fact>]
let ``with no root configured the terminal still works, which is the way back in`` () =
    task {
        let! app, _, newbie = newApp None
        use app = app
        let! code = cli app None "operator-add" [ "newbie" ]
        Assert.Equal(0, code)
        // nothing is "the root" then, so even boss can be made an operator
        let! code = cli app None "operator-add" [ "boss" ]
        Assert.Equal(0, code)
        let! code = cli app None "operator-list" []
        Assert.Equal(0, code)
        let! granted = rows app
        Assert.Equal<string list>([ "boss -"; "newbie -" ], granted)

        // and the new operators have access to the review pages, but not to the operators page
        let! review = newbie.GetRaw "/review/mal"
        Assert.Equal(HttpStatusCode.OK, review.StatusCode)
        let! page = newbie.GetRaw "/review/operators"
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode)

        let! code = cli app None "operator-remove" [ "newbie" ]
        Assert.Equal(0, code)
        let! closed = newbie.GetRaw "/review/mal"
        Assert.Equal(HttpStatusCode.NotFound, closed.StatusCode)
    }

[<Fact>]
let ``an account whose name ends in a dash (made before the rule) keeps its dashboard and operator-add finds it`` () =
    task {
        let! db = newDatabase ()
        let! app = createAppWith db [] ignore
        use app = app
        do! Db.execute app.Db "INSERT INTO users (id, username, display_name) VALUES (gen_random_uuid(), 'abc-', 'Old Timer')"

        let! response = app.Client.GetAsync "/u/abc-"
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)

        let! code = cli app None "operator-add" [ "abc-" ]
        Assert.Equal(0, code)
        let! operators = Db.dump app.Db "SELECT u.username FROM operators o JOIN users u ON u.id = o.user_id"
        Assert.Equal<string list>([ "abc-" ], operators)
    }
