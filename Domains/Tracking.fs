module FrunchyBackend.Domains.Tracking

open System

/// Lifecycle of a history submission (cr_imports.status)
[<RequireQualifiedAccess>]
type ImportStatus =
    | Pending
    | Processing
    | Completed
    | Failed

module ImportStatus =
    let toDb =
        function
        | ImportStatus.Pending -> "pending"
        | ImportStatus.Processing -> "processing"
        | ImportStatus.Completed -> "completed"
        | ImportStatus.Failed -> "failed"

    let fromDb =
        function
        | "pending" -> ImportStatus.Pending
        | "processing" -> ImportStatus.Processing
        | "completed" -> ImportStatus.Completed
        | "failed" -> ImportStatus.Failed
        | other -> failwith $"Unknown import status '{other}'"

/// What happened to one entry of a submission
[<RequireQualifiedAccess>]
type EntryOutcome =
    | Processed
    | Unresolved
    | Unsupported
    | Failed

module EntryOutcome =
    let toDb =
        function
        | EntryOutcome.Processed -> "processed"
        | EntryOutcome.Unresolved -> "unresolved"
        | EntryOutcome.Unsupported -> "unsupported"
        | EntryOutcome.Failed -> "failed"

type ImportCounts =
    { Received: int
      Processed: int
      Unresolved: int
      Unsupported: int
      Failed: int }

/// One per user and Crunchyroll episode; the newest watch state wins
type WatchEvent =
    { UserId: Guid
      CrEpisodeId: string
      WatchedAt: DateTimeOffset
      PlayheadS: int
      FullyWatched: bool
      ImportId: Guid }
