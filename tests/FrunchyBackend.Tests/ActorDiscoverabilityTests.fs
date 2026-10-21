/// Feature 021 follow-up: the actor lookup tells Megáfono `discoverable` and `indexable` from the profile's effective
/// level, and a level change asks Megáfono to look the actor up again.
module FrunchyBackend.Tests.ActorDiscoverabilityTests

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.ProfileHelpers
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// The actor lookup as (discoverable, indexable); both must be JSON booleans, not strings
let private flags (app: TestApp) (path: string) : Task<bool * bool> =
    task {
        use request = new HttpRequestMessage(HttpMethod.Get, path)
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {FakeMegafono.Token}") |> ignore
        use! response = app.Client.SendAsync request
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        let! body = response.Content.ReadAsStringAsync()
        use doc = JsonDocument.Parse body
        let d = doc.RootElement.GetProperty "discoverable"
        let i = doc.RootElement.GetProperty "indexable"
        Assert.Contains(d.ValueKind, [ JsonValueKind.True; JsonValueKind.False ])
        Assert.Contains(i.ValueKind, [ JsonValueKind.True; JsonValueKind.False ])
        return d.GetBoolean(), i.GetBoolean()
    }

let private userFlags (app: TestApp) (username: string) = flags app $"/megafono/actors?handle={username}"

let private withApp (extra: (string * string) list) : Task<TestApp> =
    task {
        let! db = newDatabase ()
        return! createFederatedAppWith db extra ignore
    }

let private refreshSubjects (app: TestApp) : Task<string list> =
    Db.dump app.Db "SELECT subject FROM fediverse_outbox WHERE kind = 'refresh_profile' ORDER BY id"

let private userId (app: TestApp) (username: string) =
    Db.scalar<Guid> app.Db "SELECT id FROM users WHERE username = @u" [ "u", box username ]

[<Fact>]
let ``a Public user is discoverable and indexable`` () =
    task {
        use! app = withApp [ defaultKey, "members" ]
        use! alice = signedUp app "sub-a" "alice"
        let! _ = setVisibility alice "public"
        let! result = userFlags app "alice"
        Assert.Equal((true, true), result)
    }

[<Fact>]
let ``a Members user is neither, by handle and by id`` () =
    task {
        use! app = withApp [ defaultKey, "public" ]
        use! alice = signedUp app "sub-a" "alice"
        let! _ = setVisibility alice "members"
        let! result = userFlags app "alice"
        Assert.Equal((false, false), result)
        let! id = userId app "alice"
        let! byId = flags app $"/megafono/actors/{id}"
        Assert.Equal((false, false), byId)
    }

[<Fact>]
let ``a Private user is neither`` () =
    task {
        use! app = withApp [ defaultKey, "public" ]
        use! alice = signedUp app "sub-a" "alice"
        let! _ = setVisibility alice "private"
        let! result = userFlags app "alice"
        Assert.Equal((false, false), result)
    }

[<Fact>]
let ``a user who never chose follows the instance default`` () =
    task {
        use! publicApp = withApp [ defaultKey, "public" ]
        use! a = signedUp publicApp "sub-a" "alice"
        let! publicResult = userFlags publicApp "alice"
        Assert.Equal((true, true), publicResult)

        use! membersApp = withApp [ defaultKey, "members" ]
        use! b = signedUp membersApp "sub-b" "bob"
        let! membersResult = userFlags membersApp "bob"
        Assert.Equal((false, false), membersResult)

        // an empty value is the app's own default: members
        use! plainApp = withApp [ defaultKey, "" ]
        use! c = signedUp plainApp "sub-c" "carol"
        let! plainResult = userFlags plainApp "carol"
        Assert.Equal((false, false), plainResult)
    }

[<Fact>]
let ``a series is discoverable and indexable`` () =
    task {
        use! app = withApp [ defaultKey, "members" ]
        do! Db.execute app.Db "INSERT INTO cr_series (id, title, source_at) VALUES ('GSERIES01', 'A Series', now())"
        let! result = flags app "/megafono/actors/series-GSERIES01"
        Assert.Equal((true, true), result)
    }

[<Fact>]
let ``a level change with federation on queues one refresh for that user, the same level again none`` () =
    task {
        use! app = withApp [ defaultKey, "members" ]
        use! alice = signedUp app "sub-a" "alice"
        use! bob = signedUp app "sub-b" "bob"
        let! aliceId = userId app "alice"

        // members is the effective level (the default), so choosing it changes nothing
        let! _ = setVisibility alice "members"
        let! none = refreshSubjects app
        Assert.Empty none

        let! _ = setVisibility alice "public"
        let! one = refreshSubjects app
        Assert.Equal<string list>([ string aliceId ], one)

        // the same level again
        let! _ = setVisibility alice "public"
        let! still = refreshSubjects app
        Assert.Equal<string list>([ string aliceId ], still)

        // another change queues a second one, still only for alice
        let! _ = setVisibility alice "private"
        let! two = refreshSubjects app
        Assert.Equal<string list>([ string aliceId; string aliceId ], two)
    }

[<Fact>]
let ``with federation off a level change queues nothing`` () =
    task {
        use! app = newApp ()
        use! alice = signedUp app "sub-a" "alice"
        let! _ = setVisibility alice "private"
        let! _ = setVisibility alice "public"
        let! count = Db.scalar<int64> app.Db "SELECT count(*) FROM fediverse_outbox" []
        Assert.Equal(0L, count)
    }
