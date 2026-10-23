module FrunchyBackend.Operations.FediverseStatus

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

/// The oldest item still waiting
type OldestPending =
    { Id: int64
      Kind: string
      Subject: string
      Attempts: int
      LastError: string option
      NextAttemptAt: DateTime }

/// An item Megáfono refused for good (state `failed`): never retried
type FailedItem =
    { Id: int64
      Kind: string
      Subject: string
      LastError: string option }

/// How many of the newest failed items the report lists
let private failedShown = 5

type Status =
    { /// (state, kind) → items; the `failed` ones are listed apart in `Failed` and `FailedByKind`
      Counts: ((string * string) * int64) list
      Oldest: OldestPending option
      /// Failed items by kind
      FailedByKind: (string * int64) list
      /// The newest few failed items, newest first
      Failed: FailedItem list }

let status (db: DbConfig) : Task<Status> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT state, kind, count(*) FROM fediverse_outbox GROUP BY 1, 2 ORDER BY 1 DESC, 2;
                SELECT id, kind, subject, attempts, last_error, next_attempt_at
                FROM fediverse_outbox WHERE state = 'pending' ORDER BY id LIMIT 1;
                SELECT kind, count(*) FROM fediverse_outbox WHERE state = 'failed' GROUP BY 1 ORDER BY 1;
                SELECT id, kind, subject, last_error FROM fediverse_outbox WHERE state = 'failed' ORDER BY id DESC LIMIT @shown;
                """,
                conn
            )

        cmd.Parameters.AddWithValue("shown", failedShown) |> ignore
        use! r = cmd.ExecuteReaderAsync()
        let counts = ResizeArray()

        while r.Read() do
            counts.Add((r.GetString 0, r.GetString 1), r.GetInt64 2)

        let! _ = r.NextResultAsync()

        let oldest =
            if r.Read() then
                Some
                    { Id = r.GetInt64 0
                      Kind = r.GetString 1
                      Subject = r.GetString 2
                      Attempts = r.GetInt32 3
                      LastError = if r.IsDBNull 4 then None else Some(r.GetString 4)
                      NextAttemptAt = r.GetDateTime 5 }
            else
                None

        let! _ = r.NextResultAsync()
        let failedByKind = ResizeArray()

        while r.Read() do
            failedByKind.Add(r.GetString 0, r.GetInt64 1)

        let! _ = r.NextResultAsync()
        let failed = ResizeArray()

        while r.Read() do
            failed.Add
                { Id = r.GetInt64 0
                  Kind = r.GetString 1
                  Subject = r.GetString 2
                  LastError = if r.IsDBNull 3 then None else Some(r.GetString 3) }

        return
            { Counts = List.ofSeq counts
              Oldest = oldest
              FailedByKind = List.ofSeq failedByKind
              Failed = List.ofSeq failed }
    }

/// Megáfono's address without anything a URL could carry besides the address (no user info, query or fragment): the
/// token and the webhook secret are never printed
let private addressOf (url: Uri) = $"{url.Scheme}://{url.Authority}{url.AbsolutePath}"

/// The report: the federation settings (never a secret), the outbox counts by state and kind, the oldest waiting item
let render (settings: Federation.FederationSettings) (s: Status) : string list =
    [ match settings with
      | Federation.Enabled values ->
          yield "FEDERATION  enabled"
          yield $"DOMAIN      {values.Domain}"
          yield $"MEGAFONO    {addressOf values.MegafonoUrl}"
      | Federation.Disabled -> yield "FEDERATION  disabled"

      yield "OUTBOX"

      let shown = s.Counts |> List.filter (fun ((state, _), _) -> state <> "failed")

      if shown.IsEmpty then
          yield "  (empty)"

      for (state, kind), count in shown do
          yield sprintf "  %-8s %-16s %6d" state kind count

      match s.Oldest with
      | Some item ->
          yield sprintf "OLDEST     #%d %s %s, %d attempts, next try %s" item.Id item.Kind item.Subject item.Attempts (item.NextAttemptAt.ToString "yyyy-MM-dd HH:mm 'UTC'")

          match item.LastError with
          | Some error -> yield $"ERROR      {error}"
          | None -> ()
      | None -> yield "OLDEST     nothing waiting"

      // Refused for good: not waiting, never retried
      if not s.FailedByKind.IsEmpty then
          yield $"FAILED     {s.FailedByKind |> List.sumBy snd} refused for good, not retried"

          for kind, count in s.FailedByKind do
              yield sprintf "  %-16s %6d" kind count

          for item in s.Failed do
              yield sprintf "  #%d %s %s: %s" item.Id item.Kind item.Subject (defaultArg item.LastError "no reason kept") ]

/// `fediverse-status`: always exits 0
let run (db: DbConfig) (settings: Federation.FederationSettings) (_: string list) : Task<int> =
    task {
        let! s = status db

        for line in render settings s do
            printfn "%s" line

        return 0
    }
