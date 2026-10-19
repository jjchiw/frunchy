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

type Status =
    { /// (state, kind) → items
      Counts: ((string * string) * int64) list
      Oldest: OldestPending option }

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
                """,
                conn
            )

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

        return { Counts = List.ofSeq counts; Oldest = oldest }
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

      if s.Counts.IsEmpty then
          yield "  (empty)"

      for (state, kind), count in s.Counts do
          yield sprintf "  %-8s %-16s %6d" state kind count

      match s.Oldest with
      | Some item ->
          yield sprintf "OLDEST     #%d %s %s, %d attempts, next try %s" item.Id item.Kind item.Subject item.Attempts (item.NextAttemptAt.ToString "yyyy-MM-dd HH:mm 'UTC'")

          match item.LastError with
          | Some error -> yield $"ERROR      {error}"
          | None -> ()
      | None -> yield "OLDEST     nothing waiting" ]

/// `fediverse-status`: always exits 0
let run (db: DbConfig) (settings: Federation.FederationSettings) (_: string list) : Task<int> =
    task {
        let! s = status db

        for line in render settings s do
            printfn "%s" line

        return 0
    }
