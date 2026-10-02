/// The whole 2026 sample enriched against real recorded MyAnimeList API responses
/// (JsonSamples/mal/, recorded by record.py; tasks T043). An early reading of SC-001.
module FrunchyBackend.Tests.SampleEnrichmentTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.FakeMal
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private recorded = File.Exists(Path.Combine(fixturesDirOf "mal", "manifest.json"))

/// Saves every 200 and 404 the real API returns, keyed like FakeMal ("search:<q>", "details:<id>"),
/// so the fixtures are exactly what Frunchy requests. Temporary failures aren't saved.
type private RecordingHandler(dir: string) =
    inherit DelegatingHandler(new SocketsHttpHandler())
    let entries = ConcurrentDictionary<string, obj>()

    member _.Save() =
        let sorted = entries |> Seq.sortBy _.Key |> Seq.map (fun kv -> kv.Key, kv.Value) |> dict
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(sorted, JsonSerializerOptions(WriteIndented = true)))
        entries.Count

    override _.SendAsync(request, cancellationToken) =
        // base.SendAsync is protected: call it before entering the task
        let sending = base.SendAsync(request, cancellationToken)

        task {
            let! (response: HttpResponseMessage) = sending
            let! (body: string) = response.Content.ReadAsStringAsync(cancellationToken)
            let key = keyOf request.RequestUri

            match int response.StatusCode with
            | 200 ->
                let name = (key.Split(':')[0]) + "-" + Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes key)).Substring(0, 12) + ".json"
                File.WriteAllText(Path.Combine(dir, name), body)
                entries[key] <- box {| file = name |}
            | 404 -> entries[key] <- box {| status = 404; body = body |}
            | _ -> ()

            return response
        }

/// Repo source folder of the MyAnimeList fixtures (not the copy in bin/)
let private sourceFixtures () =
    let rec up (d: DirectoryInfo) =
        if isNull d then failwith "repo root not found"
        elif File.Exists(Path.Combine(d.FullName, "FrunchyBackend.slnx")) then d.FullName
        else up d.Parent

    Path.Combine(up (DirectoryInfo AppContext.BaseDirectory), "tests", "FrunchyBackend.Tests", "JsonSamples", "mal")

/// Records the fixtures from the real API. Skipped unless asked for:
///   FRUNCHY_RECORD_MAL=1 bella run -- dotnet test --filter-method "*record the 2026 sample*"
[<Fact>]
let ``record the 2026 sample from the real MyAnimeList API`` () =
    task {
        let clientId = Environment.GetEnvironmentVariable "Frunchy__Mal__ClientId"

        Assert.SkipWhen(
            String.IsNullOrEmpty clientId || Environment.GetEnvironmentVariable "FRUNCHY_RECORD_MAL" <> "1",
            "recording only: FRUNCHY_RECORD_MAL=1 bella run -- dotnet test …"
        )

        let dir = sourceFixtures ()
        Directory.CreateDirectory dir |> ignore

        for old in Directory.EnumerateFiles(dir, "*.json") do
            File.Delete old

        let recorder = new RecordingHandler(dir)
        let! db = newDatabase ()

        use! app =
            createAppWith
                db
                [ "Frunchy:Mal:BaseUrl", "https://api.myanimelist.net/v2/"
                  "Frunchy:Mal:ClientId", clientId
                  "Frunchy:Mal:RequestsPerSecond", "0.8"
                  "Frunchy:Mal:RetryBaseDelay", "00:00:02"
                  "Frunchy:Mal:AttemptTimeout", "00:00:20" ]
                (fun (b: WebApplicationBuilder) ->
                    b.Services
                        .AddHttpClient(MalClient.ClientName)
                        .ConfigurePrimaryHttpMessageHandler(fun () -> recorder :> HttpMessageHandler)
                        .SetHandlerLifetime(Threading.Timeout.InfiniteTimeSpan)
                    |> ignore)

        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        try
            let! _ = enrichAll app
            ()
        finally
            recorder.Save() |> ignore

        let saved = recorder.Save()
        Assert.True(saved > 100, $"only {saved} responses recorded")
    }

[<Fact>]
let ``the 2026 sample against recorded MyAnimeList responses`` () =
    task {
        Assert.True(recorded, "record JsonSamples/mal first (see the recording test above)")
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        app.Mal.LoadRecorded "mal" |> ignore
        let! _ = enrichAll app

        // Every season reaches a definite state; nothing is left pending or failed
        let! open' = Db.scalar<int64> app.Db "SELECT count(*) FROM cr_season_matches WHERE status IN ('pending', 'failed')" []
        Assert.Equal(0L, open')

        // The report, for review: every season with what it was matched to
        let! report =
            Db.dump
                app.Db
                """
                SELECT m.status || ' ' || coalesce(m.method, '-') || ' | ' || coalesce(sr.title, sr.id) || ' / ' || coalesce(se.title, '')
                       || ' (#' || coalesce(se.season_number::text, '-') || ', ' || coalesce(se.audio_locale, '-') || ') → '
                       || coalesce(a.mal_id::text || ' ' || coalesce(a.title_english, a.title) || ' [' || coalesce(a.type, '?') || ', '
                                   || coalesce(a.year::text, '?') || ']', coalesce(m.reason, '-'))
                FROM cr_season_matches m
                JOIN cr_seasons se ON se.id = m.season_id
                JOIN cr_series sr ON sr.id = se.series_id
                LEFT JOIN mal_anime a ON a.mal_id = m.mal_id
                ORDER BY m.status, sr.title, se.season_number
                """

        let! total = Db.count app.Db "cr_season_matches"
        let! matched = Db.scalar<int64> app.Db "SELECT count(*) FROM cr_season_matches WHERE status = 'matched'" []
        let rate = 100.0 * float matched / float total

        File.WriteAllLines(
            Path.Combine(AppContext.BaseDirectory, "sample-enrichment-report.txt"),
            $"matched {matched}/{total} = {rate:F1}%%" :: report
        )

        // Known cases (research R2)
        let! frieren = matchOf app.Db "GYE5CQMQ5"
        Assert.Equal("GYE5CQMQ5 matched 52991 search -", frieren)
        let! jjk2 = matchOf app.Db "G6MGC3778"
        Assert.Equal("G6MGC3778 matched 51009 link -", jjk2)
        let! jjk0 = matchOf app.Db "GMSE00194450JAJP"
        Assert.StartsWith("GMSE00194450JAJP matched 48561", jjk0)
        let! tamerDub = matchOf app.Db "GYDQCGK5Q"
        Assert.EndsWith("dub GRK5CNJ14", tamerDub)
        let! rollDub = matchOf app.Db "GS00365593ES419"
        Assert.EndsWith("dub GS00365593JAJP", rollDub)

        // Cases that needed a rule (research R2/R13): a side-story OVA, a special inside the sequel
        // chain, an OVA hanging off a later part, a same-name OVA from 1989, a synonym-only rival,
        // "gray" (nsfw) entries, a year in the title
        let! magusOva = matchOf app.Db "GRDQ4KK1Y"
        Assert.StartsWith("GRDQ4KK1Y matched 32902", magusOva)
        let! mushoku3 = matchOf app.Db "GS00374452JAJP"
        Assert.StartsWith("GS00374452JAJP matched 59193", mushoku3)
        let! moriartyOva = matchOf app.Db "GS00363167JAJP"
        Assert.StartsWith("GS00363167JAJP matched 49733", moriartyOva)
        let! bucchigiri = matchOf app.Db "GR5VCDPQ0"
        Assert.StartsWith("GR5VCDPQ0 matched 55358", bucchigiri)
        let! outcast3 = matchOf app.Db "GS00346546ZHCN"
        Assert.StartsWith("GS00346546ZHCN matched 38536", outcast3)
        let! rollOver = matchOf app.Db "GS00365593JAJP"
        Assert.StartsWith("GS00365593JAJP matched 61587", rollOver)
        let! dai = matchOf app.Db "G64G2VG8R"
        Assert.StartsWith("G64G2VG8R matched 40906", dai)

        // SC-001's target is 85% on the real catalog; the sample reached 76/78 when recorded
        Assert.True(rate >= 90.0, $"automatic match rate {rate:F1}%% ({matched}/{total})")
    }
