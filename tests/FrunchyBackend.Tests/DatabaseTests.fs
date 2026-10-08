module FrunchyBackend.Tests.DatabaseTests

open Xunit
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

[<Fact>]
let ``schema bootstrap is idempotent`` () =
    task {
        let! db = newDatabase ()
        do! initializeSchema db
        do! initializeSchema db
        let! tables =
            Db.scalar<int64>
                db
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = ANY(@names)"
                [ "names",
                  box
                      [| "users"; "cr_imports"; "cr_import_pages"; "cr_import_entry_issues"; "cr_series"; "cr_seasons"; "cr_episodes"; "watch_events"
                         // feature 003
                         "mal_anime"; "mal_tags"; "mal_anime_tags"; "mal_relations"; "mal_searches"; "cr_season_matches" |] ]
        Assert.Equal(14L, tables)
    }

[<Fact>]
let ``raw pages are stored as json not jsonb`` () =
    task {
        let! db = newDatabase ()
        let! dataType =
            Db.scalar<string>
                db
                "SELECT data_type FROM information_schema.columns WHERE table_name = 'cr_import_pages' AND column_name = 'body'"
                []
        Assert.Equal("json", dataType)
    }

[<Fact>]
let ``Jikan responses are stored as json not jsonb`` () =
    task {
        let! db = newDatabase ()
        let! types =
            Db.scalar<string>
                db
                "SELECT string_agg(data_type, ',' ORDER BY table_name) FROM information_schema.columns WHERE table_name IN ('mal_anime', 'mal_searches') AND column_name = 'raw'"
                []
        Assert.Equal("json,json", types)
    }

[<Fact>]
let ``legacy layout is refused with reset instructions`` () =
    task {
        let! db = newEmptyDatabase ()
        do! Db.execute db "CREATE TABLE import_records (cr_import_id uuid PRIMARY KEY)"
        let! ex = Assert.ThrowsAsync<LegacySchemaException>(fun () -> initializeSchema db :> System.Threading.Tasks.Task)
        Assert.Contains("dropdb frunchydb", ex.Data0)
    }
