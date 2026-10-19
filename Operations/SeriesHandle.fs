module FrunchyBackend.Operations.SeriesHandle

open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

/// Crunchyroll's id alphabet, checked before any query
let private seriesIdPattern = Regex(@"\A[A-Z0-9]{6,20}\z", RegexOptions.CultureInvariant)

/// `series-handle <seriesId> <handle>` (feature 015, FR-002): set the fediverse handle of a series by hand, for a
/// series account that hasn't federated yet. Works with federation off too: it only writes the stored handle.
///
/// Exit codes: 0 set (or already that handle), 1 usage or a handle that breaks the rule (1 to 30 characters of
/// a-z, 0-9, `_` and `-`, no dash at either end), 2 unknown series, 3 refused because the series' thread has been
/// published (its handle may be known to remote servers), 4 the handle is taken by another series or a username.
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ seriesId; typed ] ->
            let handle = typed.Trim().ToLowerInvariant()

            if not (seriesIdPattern.IsMatch seriesId) then
                eprintfn "'%s' is not a series id." seriesId
                return 1
            elif not (Fediverse.isValidHandle handle) then
                eprintfn "'%s' is not a valid handle: 1 to 30 characters of a-z, 0-9, _ and -, with no dash at either end." typed
                return 1
            else
                use connection = getConnection db
                do! connection.OpenAsync()

                match! Fediverse.setSeriesHandle connection seriesId handle with
                | Fediverse.HandleSet ->
                    printfn "Series %s now has the handle %s." seriesId handle
                    return 0
                | Fediverse.HandleUnchanged ->
                    printfn "Series %s already has the handle %s; nothing changed." seriesId handle
                    return 0
                | Fediverse.UnknownSeries ->
                    eprintfn "Unknown series %s." seriesId
                    return 2
                | Fediverse.AlreadyFederated ->
                    eprintfn "Series %s has already federated (its thread is published), so its handle can't change; nothing changed." seriesId
                    return 3
                | Fediverse.HandleTaken ->
                    eprintfn "The handle %s is already used by another series or a username; nothing changed." handle
                    return 4
        | _ ->
            eprintfn "Usage: series-handle <seriesId> <handle>"
            return 1
    }
