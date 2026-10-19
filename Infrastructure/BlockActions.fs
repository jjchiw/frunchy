/// Feature 015 (US6): block management, shared by the account page (a user's blocks) and the operator page
/// (instance blocks). The calls to Megáfono are synchronous: the answer decides the notice, and a security event is
/// recorded only for a change that happened (201 on add, 204 on remove), never for a repeat or a refusal.
module FrunchyBackend.Infrastructure.BlockActions

open System
open System.Net.Http
open System.Threading.Tasks
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Infrastructure.MegafonoClient

/// Whose blocks: a user's own, or the whole instance's
type Scope =
    | User of Guid
    | Instance

/// What the block list answered: the blocks, or nothing (Megáfono can't be asked right now)
type Listing =
    | Loaded of BlockItem list
    | Unavailable

let list (client: HttpClient) (scope: Scope) : Task<Listing> =
    task {
        let! result =
            match scope with
            | User id -> listBlocks client (string id)
            | Instance -> listInstanceBlocks client

        return if result.IsSuccess then Loaded(blocksOf result) else Unavailable
    }

/// A change made, as its event, or nothing (a repeat or a refusal)
let private eventFor (isAdd: bool) (key: string) =
    match isAdd, key with
    | true, "block-added" -> Some SecurityEventType.FediverseBlockAdded
    | false, "block-removed" -> Some SecurityEventType.FediverseBlockRemoved
    | _ -> None

/// The account the change is recorded on: the user who made it (the operator, for an instance block)
let private recordEvent (db: DbConfig) (userId: Guid) (event: SecurityEventType) : Task<unit> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()
        do! SecurityEvents.record connection tx userId event None FrunchyBackend.Domains.Identity.Actor.User
        do! tx.CommitAsync()
    }

/// Add a block from what was typed. Returns the notice key; `by` is who gets the security event.
let add (db: DbConfig) (client: HttpClient) (scope: Scope) (by: Guid) (typed: string) : Task<string> =
    task {
        match parseBlockTarget typed with
        | None -> return "block-invalid"
        | Some target ->
            let! result =
                match scope with
                | User id -> addBlock client (string id) target
                | Instance -> addInstanceBlock client target

            let key = blockNoticeKey true result

            match eventFor true key with
            | Some event -> do! recordEvent db by event
            | None -> ()

            return key
    }

/// Lift a block the list showed: `kind` is "actor" or "domain", `target` its value. Returns the notice key.
let remove (db: DbConfig) (client: HttpClient) (scope: Scope) (by: Guid) (kind: string) (target: string) : Task<string> =
    task {
        // The value is read by the same shape rule as a typed one, and has to be what the list said it was
        let parsed =
            match kind, parseBlockTarget target with
            | "actor", Some(MegafonoClient.Actor _ as actor) -> Some actor
            | "domain", Some(MegafonoClient.Domain _ as domain) -> Some domain
            | _ -> None

        match parsed with
        | None -> return "block-invalid"
        | Some target ->
            let! result =
                match scope with
                | User id -> removeBlock client (string id) target
                | Instance -> removeInstanceBlock client target

            let key = blockNoticeKey false result

            match eventFor false key with
            | Some event -> do! recordEvent db by event
            | None -> ()

            return key
    }
