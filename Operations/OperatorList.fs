module FrunchyBackend.Operations.OperatorList

open System.Threading.Tasks
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

/// The output: the root line, a header, then a line per operator
let render (root: string option) (rows: OperatorActions.OperatorRow list) : string list =
    [ yield sprintf "Root operator (from configuration): %s" (root |> Option.defaultValue "none")
      yield sprintf "%-30s %-30s %-30s %s" "USERNAME" "DISPLAY NAME" "ADDED BY" "ADDED (UTC)"

      for row in rows do
          yield sprintf "%-30s %-30s %-30s %s" row.Username row.DisplayName (row.AddedBy |> Option.defaultValue "-") (row.AddedAt.ToString "yyyy-MM-dd HH:mm") ]

/// `operator-list`: the root (from configuration) and each operator in the table
let run (db: DbConfig) (root: string option) (_: string list) : Task<int> =
    task {
        let! rows = OperatorActions.list db

        for line in render root rows do
            printfn "%s" line

        return 0
    }
