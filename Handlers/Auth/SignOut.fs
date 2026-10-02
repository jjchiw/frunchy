module FrunchyBackend.Handlers.Auth.SignOut

open Oxpecker
open FrunchyBackend.Infrastructure

// ---------------------------------------------------------------------------
// Handler: ends this browser's session only (FR-006); the extension's sync key keeps working
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    fun ctx ->
        task {
            let! _ = WebAuth.readValidatedForm ctx
            do! WebAuth.signOut ctx
            return! WebAuth.redirectTo ctx "/"
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = POST [ route "/signout" handler ]
