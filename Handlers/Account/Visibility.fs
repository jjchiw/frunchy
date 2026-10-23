/// Feature 017 (US2): the owner chooses whether their profile is public, for members or private (021 added members). The choice is stored in
/// `profile_settings` (no row means the instance default) and outranks the default from then on.
module FrunchyBackend.Handlers.Account.Visibility

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Npgsql
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Data access: the upsert and its security event share one transaction
// ---------------------------------------------------------------------------

let private save
    (db: DbConfig)
    (instanceDefault: Profiles.Visibility)
    (federated: bool)
    (userId: Guid)
    (visibility: Profiles.Visibility)
    : Task<unit> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        // The level in force before this save (the choice, else the instance default), read under a lock on the row
        use previousCmd =
            new NpgsqlCommand("SELECT visibility FROM profile_settings WHERE user_id = @id FOR UPDATE", connection, tx)

        previousCmd.Parameters.AddWithValue("id", userId) |> ignore

        let! previous =
            task {
                match! previousCmd.ExecuteScalarAsync() with
                | :? string as stored -> return Profiles.effective instanceDefault (Profiles.Visibility.tryParse stored)
                | _ -> return instanceDefault
            }

        use cmd =
            new NpgsqlCommand(
                """
                INSERT INTO profile_settings (user_id, visibility) VALUES (@id, @visibility)
                ON CONFLICT (user_id) DO UPDATE SET visibility = EXCLUDED.visibility, updated_at = now()
                """,
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("id", userId) |> ignore
        cmd.Parameters.AddWithValue("visibility", Profiles.Visibility.toDb visibility) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()

        // The event names the level it became (the older profile_visibility_changed rows keep their label)
        let event =
            match visibility with
            | Profiles.Public -> SecurityEventType.ProfileMadePublic
            | Profiles.Members -> SecurityEventType.ProfileMadeMembers
            | Profiles.Private -> SecurityEventType.ProfileMadePrivate

        do! SecurityEvents.record connection tx userId event None Actor.User

        // The actor's discoverable and indexable flags follow the level: Megáfono looks the profile up again
        // (feature 015), in the same transaction, and only when the level really changed
        if federated && previous <> visibility then
            do! Fediverse.enqueue connection tx "refresh_profile" (string userId)

        do! tx.CommitAsync()
    }

// ---------------------------------------------------------------------------
// Handler: signed in, then the antiforgery check and the form, then the value rule. Nothing changes on a bad value.
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    WebAuth.requireSignedIn (fun userId ctx ->
        task {
            let! form = WebAuth.readValidatedForm ctx

            // Exactly the three words the radios send (not trimmed or case-folded further: `formValue` trims)
            let choice =
                match WebAuth.formValue form "visibility" with
                | "public" -> Some Profiles.Public
                | "members" -> Some Profiles.Members
                | "private" -> Some Profiles.Private
                | _ -> None

            match choice with
            | Some visibility ->
                let federated = Fediverse.isEnabled (ctx.GetService<Federation.FederationSettings>())
                let instanceDefault = (ctx.GetService<Profiles.ProfileSettings>()).DefaultVisibility
                do! save (ctx.GetService<DbConfig>()) instanceDefault federated userId visibility
                return! WebAuth.redirectTo ctx "/account?msg=visibility-saved#visibility"
            | None -> return! WebAuth.redirectTo ctx "/account?msg=visibility-invalid#visibility"
        })

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = POST [ route "/account/visibility" handler |> configureEndpoint _.RequireRateLimiting("account-write") ]
