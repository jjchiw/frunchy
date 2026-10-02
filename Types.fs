module FrunchyBackend.Types

open FSharp.Data
open System
//[<Literal>]
//let ResolutionFolder = __SOURCE_DIRECTORY__

//type PostHistoryTo = JsonProvider<"./JsonSamples/PostHistory.json", ResolutionFolder=ResolutionFolder>

type PostHistoryEpisodeMetadata =
    { season_title: string
      duration_ms: int }

type PostHistoryPanel =
    { title: string
      episode_metadata: PostHistoryEpisodeMetadata
      descripion: string }

type PostHistoryBody =
    { id: string
      date_played: DateTime
      parent_id: string
      parent_type: string
      playhead: int
      fully_watched: bool
      panel: PostHistoryPanel option }

type PostHistoryTo = { total: int; data: PostHistoryBody[] }
