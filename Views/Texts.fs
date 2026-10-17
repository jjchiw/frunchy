module FrunchyBackend.Views.Texts

open System.Globalization

/// Every user-facing string of the web UI. Adding a field forces a value in each language.
type Texts =
    { /// BCP-47 language of this text set, rendered as <html lang>
      Lang: string
      Culture: CultureInfo
      SiteName: string
      Footer: string

      HomeTitle: string
      HomeHeadline: string
      HomeIntro: string
      HomeHowToSyncTitle: string
      HomeSteps: string list
      HomeProfilesNote: string

      NotFoundTitle: string
      NotFoundBody: string
      ErrorTitle: string
      ErrorBody: string
      BackHome: string

      // Public dashboard (/u/{username})
      PublicProfileNote: string
      LastSync: string
      NeverSynced: string
      NewestEpisode: string
      SyncInProgress: string
      StatSeries: string
      StatEpisodes: string
      StatHours: string
      StatDays: string
      RecentTitle: string
      EpisodeLabel: int -> string
      SpecialLabel: string
      Minutes: int -> string
      InProgress: string
      // MyAnimeList enrichment on the dashboard (feature 003)
      TopGenresTitle: string
      TopGenresEpisodes: int64 -> string
      TopGenresCoverage: int -> string
      MyAnimeListLink: string
      ScoreLabel: string
      EmptyTitle: string
      EmptyBody: string
      UserNotFoundTitle: string
      UserNotFoundBody: string

      // Anime DNA (/u/{username}/dna, feature 004)
      DnaTitle: string
      DnaHeading: string -> string
      DnaIntro: string
      DnaLink: string
      DashboardLink: string
      CopyLink: string
      LinkCopied: string
      CopyLinkFallback: string
      DnaWatchTime: string
      DnaDaysOfLife: decimal -> string
      DnaCoverageTile: string
      DnaCoverageCaption: string
      DnaCoverage: decimal -> string
      DnaSectionNotEnough: string
      DnaStudioTitle: string
      DnaRunnerUp: string
      DnaDecadeTitle: string
      DnaDecade: int -> string
      DnaDecadeCoverage: decimal -> string
      DnaPaceTitle: string
      DnaPaceUnit: string
      DnaSteady: string
      DnaBursts: string
      DnaPaceDetail: int64 -> int -> string
      DnaPaceChart: int64 -> string
      DnaPaceNotEnough: string
      DnaInsigniaTitle: string
      DnaInsigniaEpisodes: int64 -> int option -> string
      DnaInsigniaHours: decimal -> string
      /// MyAnimeList season (winter, spring, summer, fall) and year
      DnaSeason: string -> int -> string
      DnaGenresTitle: string
      DnaGenresNote: string
      DnaOthers: string
      /// "Top:" before a genre's top anime, which is a link
      TopAnimePrefix: string
      DnaFormatsTitle: string
      DnaEmptyTitle: string
      DnaEmptyCount: int64 -> string
      DnaEmptyBody: string
      DnaEmptyAction: string

      // Navigation and generic web errors (feature 002)
      NavSignIn: string
      NavAccount: string
      NavSignOut: string
      SkipToContent: string
      TooManyRequestsTitle: string
      TooManyRequestsBody: string
      FormExpiredTitle: string
      FormExpiredBody: string

      // Sign in / sign up (feature 002, US1)
      SignInTitle: string
      SignInIntro: string
      SignInWith: string -> string
      SignInUnavailable: string
      SignInFailed: string
      SignUpTitle: string
      SignUpIntro: string
      InviteCodeLabel: string
      UsernameLabel: string
      UsernameHint: string
      DisplayNameLabel: string
      CreateAccountButton: string
      HaveAccountLink: string
      InviteInvalid: string
      UsernameInvalid: string
      UsernameTaken: string
      DisplayNameInvalid: string
      IdentityAlreadyUsed: string

      // Account page and "Connect extension" (feature 002, US1)
      AccountTitle: string
      AccountUsername: string
      AccountDisplayName: string
      AccountDashboard: string
      AccountSyncKey: string
      AccountKeyActive: string
      AccountKeyMissing: string
      AccountSignInMethods: string
      AccountBinding: string
      AccountBound: string
      AccountUnbound: string
      ConnectExtension: string
      KeyConfirmTitle: string
      KeyConfirmBody: string
      KeyConfirmButton: string
      KeyShownTitle: string
      KeyShownBody: string
      KeyShownOnce: string
      BackToAccount: string

      // Claim an operator-created account (feature 002, US2)
      ClaimTitle: string
      ClaimIntro: string
      SyncKeyLabel: string
      ClaimButton: string
      ClaimRefused: string
      BackToSignUp: string

      // Managing the account (feature 002, US3)
      SaveButton: string
      DisplayNameSaved: string
      BindingResetButton: string
      BindingResetDone: string
      LinkProvider: string -> string
      UnlinkButton: string
      IdentityLinkedNotice: string
      IdentityUnlinkedNotice: string
      LinkTaken: string
      LinkDuplicate: string
      UnlinkLast: string
      ReconfirmFailed: string
      RecentSyncsTitle: string
      NoSyncsYet: string
      SyncStatus: string -> string
      SyncCounts: int -> int -> int -> int -> string
      SecurityEventsTitle: string
      SecurityEventLabel: string -> string
      ByOperator: string

      // Delete my account (feature 002, US4)
      DeleteAccountLink: string
      DeleteTitle: string
      DeleteWarning: string
      DeleteTypeUsername: string -> string
      DeleteButton: string
      DeleteMismatch: string
      AccountDeletedNotice: string

      // T017
      HomeNoPassword: string
      // T019
      AccountProfileTitle: string
      AccountUsernameLocked: string
      CopyKey: string
      KeyCopied: string
      DeleteItems: string list
      // T016
      /// A recent card whose season has no MyAnimeList match
      NotOnMyAnimeList: string
      /// How far a partly watched episode got, in percent
      WatchedPercent: int -> string
      // T018
      /// Stands in for the username in the sign-up page's address preview while the field is empty
      UsernamePreviewPlaceholder: string
      DisplayNameHint: string

      // Year in review (/u/{username}/year, feature 005)
      YearTitle: int -> string
      YearHeading: string -> int -> string
      YearIntro: string
      YearLink: string
      YearRewatchNote: string
      YearPickerLabel: string
      YearHoursCaption: decimal -> string
      YearStatSeries: string
      YearTopSeriesTitle: string
      YearTopSeriesEpisodes: int64 -> string
      YearMonthsTitle: string
      YearBusiestMonth: string -> decimal -> string
      YearMonthsChart: int -> string
      YearSplitTitle: string
      YearNewSeries: int64 -> string
      YearContinuedSeries: int64 -> string
      YearStreakTitle: string
      YearStreakDays: int -> string
      YearStreakRange: string -> string -> string
      YearStreakNone: string
      YearGenresTitle: string
      YearStudiosTitle: string
      YearFormatsTitle: string
      YearEmptyTitle: int -> string
      YearEmptyCount: int64 -> string
      YearEmptyBody: string

      // Anime page (/u/{username}/anime/{seriesId}, feature 006)
      AnimeSeasonsTitle: string
      AnimeSeasonNumber: int -> string
      AnimeRewatchNote: string
      AnimeFirstWatch: string
      AnimeLatestWatch: string
      AnimeWatchOnCrunchyroll: string
      /// fully watched, known episodes of a season (the known ones come from any user's history)
      AnimeKnownEpisodes: int64 -> int64 -> string
      AnimeOtherEpisodes: string
      AnimeDub: string
      AnimeStudio: string -> string
      AnimeMalEpisodes: int -> string
      /// MyAnimeList's airing status as stored, localized; unknown values are shown as stored
      AnimeStatus: string -> string
      AnimeEpisodeWatched: string
      /// A Crunchyroll audio locale ("ja-JP") as a language name; unknown codes are shown as stored
      AudioLanguage: string -> string
      // MyAnimeList review (/review/mal, feature 007)
      ReviewTitle: string
      ReviewIntro: string
      ReviewManualTitle: string
      ReviewManualIntro: string
      ReviewTabOpen: string
      ReviewTabManual: string
      ReviewFilterAll: string
      ReviewFilterAmbiguous: string
      ReviewFilterNoMatch: string
      ReviewFilterFailed: string
      ReviewColSeason: string
      ReviewColStatus: string
      ReviewColUsers: string
      ReviewColFullyWatched: string
      ReviewColCandidates: string
      ReviewColDecision: string
      ReviewColDecidedAt: string
      ReviewUseThis: string
      ReviewSetButton: string
      ReviewNoneButton: string
      ReviewRetryButton: string
      ReviewAutoButton: string
      ReviewMalIdLabel: string
      ReviewMalIdHint: string
      ReviewNoCandidates: string
      ReviewManualNone: string
      ReviewEmpty: string
      ReviewManualEmpty: string
      ReviewMore: int -> string
      ReviewAccountLink: string
      /// The notice for a `msg` key (set, none, retry, auto, unknown-season, unknown-anime, unavailable, not-open, bad-input)
      ReviewNotice: string -> string

      // Operators (/review/operators, feature 008)
      OperatorsTitle: string
      OperatorsIntro: string
      OperatorsRootLabel: string
      OperatorsRootNote: string
      OperatorsColUser: string
      OperatorsColAddedBy: string
      OperatorsColAddedAt: string
      /// "Added by" when no root account is on record: added from the terminal, or the granting account was deleted
      OperatorsAddedFromServer: string
      OperatorsAddLabel: string
      OperatorsAddHint: string
      OperatorsAddButton: string
      OperatorsRemoveButton: string
      OperatorsRemoveLabel: string -> string
      OperatorsEmpty: string
      /// The notice for a `msg` key (added, removed, unknown-user, is-root, already-operator, not-operator, bad-input)
      OperatorNotice: string -> string
      /// Account page: the signed-in user is an operator
      OperatorStatus: string
      OperatorsAccountLink: string

      // Episode page and comments (feature 009)
      /// Page title from the series title and the episode label
      EpisodePageTitle: string -> string -> string
      EpisodeWatchOnCrunchyroll: string
      EpisodeSeriesOnCrunchyroll: string
      CommentsHeading: string
      CommentsEmpty: string
      CommentEdited: string
      CommentHiddenPlaceholder: string
      /// Shown to operators only: hiding a comment hides it on Frunchy, not elsewhere
      CommentModerationNote: string
      CommentShowSpoiler: string
      CommentFormLabel: string
      CommentSpoilerLabel: string
      CommentSubmit: string
      CommentSignInPrompt: string
      /// The message for an error key (empty, too-long, bad-id)
      CommentError: string -> string
      CommentEditTitle: string
      CommentSave: string
      CommentCancel: string
      CommentDelete: string
      CommentDeleteConfirm: string
      CommentHide: string
      CommentUnhide: string
      /// The warning stored with a spoiler comment: season display number, episode number, special label
      SpoilerSummary: string option -> int option -> string option -> string

      // Taste compatibility (feature 011): the dashboard card and /u/{username}/compare.
      // No text prints a score or a percentage of the level.
      /// "You and {name}"
      CompatTitle: string -> string
      /// The compare page's title, "Compare with {name}"
      CompatPageTitle: string -> string
      /// The level's name for 1..5: Low, Some, Good, High, Very high
      CompatLevelName: int -> string
      /// "{Level} compatibility"
      CompatLevel: int -> string
      /// The meter's accessible label: "{Level}: {n} of 5"
      CompatMeterLabel: int -> string
      /// "{n} series in common", "No series in common yet" at 0
      CompatSharedCount: int -> string
      /// Name, shared count, first shared genre: "You and {name} share {n} series and a soft spot for {genre}"
      CompatSummary: string -> int -> string option -> string
      /// One plain sentence on what the level rests on
      CompatExplain: string
      /// Said on the page when a side has no MyAnimeList genres
      CompatNoGenres: string
      CompatYou: string
      /// "{n} series", for a person's total and for a studio's shared series
      CompatSeriesCount: int -> string
      CompatTimeTitle: string
      CompatSharedTitle: string
      /// "In {name}'s history, not yours"
      CompatTheirsTitle: string -> string
      CompatStudiosTitle: string
      /// "and {n} more"
      CompatMore: int -> string
      /// "Based on {p}% of {name}'s watch time." (name, percent)
      CompatCoverage: string -> decimal -> string
      CompatNotEnoughViewer: string
      CompatNotEnoughOwner: string -> string
      CompatNotEnoughBoth: string -> string
      /// The card's link to the compare page
      CompatSeeComparison: string

      // Charts and trends (feature 012): /charts, /charts/season, /charts/all and the people counts.
      // The season label is `DnaSeason` and "Watch on Crunchyroll" is `EpisodeWatchOnCrunchyroll`.
      /// The header link
      NavCharts: string
      ChartsTitle: string
      ChartsTabWeek: string
      ChartsTabSeason: string
      ChartsTabAll: string
      /// "{n} people · {m} episodes" (people, episodes)
      ChartsPeopleEpisodes: int -> int -> string
      /// "Up {n}", "Down {n}"
      ChartsMoveUp: int -> string
      ChartsMoveDown: int -> string
      ChartsMoveSame: string
      ChartsMoveNew: string
      /// The trend's accessible label: "Last 8 weeks: {values}", values oldest first
      ChartsTrendLabel: int list -> string
      /// An episode counts once, in the week (or season) of its latest watch
      ChartsLatestNote: string
      /// "Series appear once {n} people have watched them"
      ChartsMinNote: int -> string
      ChartsEmpty: string
      /// "Watched by {n} people on Frunchy" on the anime and episode pages
      ChartsWatchedBy: int -> string
      /// The series page's browser title (feature 013): {series title}
      SeriesPageTitle: string -> string
      /// "{n} known episodes" (the summary of a collapsed season, and each season's count)
      SeriesKnownEpisodes: int -> string
      /// "Watched by {n} people this week"
      SeriesWatchedWeek: int -> string
      /// Says the list is what Frunchy knows, not the season's full Crunchyroll list
      SeriesKnownNote: string
      /// The chart row's link to the viewer's own anime page
      ChartsYourHistory: string
      /// A link from a person's anime page to the series page
      SeriesPageLink: string
      /// "{n} comments", the accessible label of an episode's comment count
      SeriesCommentsLabel: int -> string }

let en =
    { Lang = "en"
      Culture = CultureInfo.GetCultureInfo "en"
      SiteName = "Frunchy"
      Footer = "Frunchy, a Last.fm for anime"

      HomeTitle = "Home"
      HomeHeadline = "Your anime identity, in numbers"
      HomeIntro = "Frunchy turns your Crunchyroll watch history into statistics you can share."
      HomeHowToSyncTitle = "How to sync"
      HomeSteps =
        [ "Install the Frunchy browser extension and paste your sync key in its options."
          "Open your watch history on crunchyroll.com."
          "The extension sends it to Frunchy; your public dashboard updates in a moment." ]
      HomeProfilesNote = "Dashboards are public: anyone with the link to a profile can see its numbers."

      NotFoundTitle = "Page not found"
      NotFoundBody = "There's nothing at this address."
      ErrorTitle = "Something went wrong"
      ErrorBody = "We couldn't complete your request. Please try again later."
      BackHome = "Back to home"

      PublicProfileNote = "This profile is public: anyone with the link can see these numbers."
      LastSync = "Last sync"
      NeverSynced = "Not synced yet"
      NewestEpisode = "Newest episode"
      SyncInProgress = "A sync is being processed; the numbers update when it finishes."
      StatSeries = "Series"
      StatEpisodes = "Episodes watched"
      StatHours = "Hours watched"
      StatDays = "Days of life"
      RecentTitle = "Recently watched"
      EpisodeLabel = fun n -> $"Episode {n}"
      SpecialLabel = "Special"
      Minutes = fun m -> $"{m} min"
      InProgress = "In progress"
      TopGenresTitle = "Top genres"
      TopGenresEpisodes =
        fun n -> if n = 1L then "1 episode" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "en")} episodes"""
      TopGenresCoverage =
        fun p -> $"Based on {p}%% of fully watched episodes. An episode counts in each of its genres."
      MyAnimeListLink = "MyAnimeList"
      ScoreLabel = "MyAnimeList score"
      EmptyTitle = "No history yet"
      EmptyBody =
        "Install the Frunchy extension, paste your sync key in its options and open your Crunchyroll history: your numbers appear here a moment later."
      UserNotFoundTitle = "User not found"
      UserNotFoundBody = "There's no Frunchy profile with this name."

      DnaTitle = "Anime DNA"
      DnaHeading = fun name -> $"{name}'s Anime DNA"
      DnaIntro = "What this profile watches, from its Crunchyroll history and MyAnimeList."
      DnaLink = "Anime DNA"
      DashboardLink = "Dashboard"
      CopyLink = "Copy link"
      LinkCopied = "Link copied"
      CopyLinkFallback = "Copy this address"
      DnaWatchTime = "Watch time"
      DnaDaysOfLife = fun d -> $"""{d.ToString("N1", CultureInfo.GetCultureInfo "en")} days of life"""
      DnaCoverageTile = "MyAnimeList coverage"
      DnaCoverageCaption = "of the watch time has MyAnimeList details"
      DnaCoverage = fun p -> $"""Based on {p.ToString("0.#", CultureInfo.GetCultureInfo "en")}%% of the watch time."""
      DnaSectionNotEnough = "Not enough MyAnimeList data yet."
      DnaStudioTitle = "Favourite studio"
      DnaRunnerUp = "Runner-up"
      DnaDecadeTitle = "Dominant decade"
      DnaDecade = fun y -> $"{y}s"
      DnaDecadeCoverage = fun p -> $"""Based on {p.ToString("0.#", CultureInfo.GetCultureInfo "en")}%% of the watch time, the part with a known release year."""
      DnaPaceTitle = "Viewing pace, last 90 days"
      DnaPaceUnit = "episodes a day"
      DnaSteady = "Steady"
      DnaBursts = "In bursts"
      DnaPaceDetail = fun episodes weeks -> $"""{episodes.ToString("N0", CultureInfo.GetCultureInfo "en")} episodes, active in {weeks} of 13 weeks"""
      DnaPaceChart = fun episodes -> $"""Episodes per day over the last 90 days, {episodes.ToString("N0", CultureInfo.GetCultureInfo "en")} in total"""
      DnaPaceNotEnough = "Not enough recent data: fewer than 10 episodes in the last 90 days."
      DnaInsigniaTitle = "Insignia anime"
      DnaInsigniaEpisodes =
        fun n total ->
            match total with
            | Some t -> $"""{n.ToString("N0", CultureInfo.GetCultureInfo "en")} of {t} episodes watched"""
            | None -> if n = 1L then "1 episode watched" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "en")} episodes watched"""
      DnaInsigniaHours = fun h -> $"""{h.ToString("N1", CultureInfo.GetCultureInfo "en")} hours"""
      DnaSeason =
        fun season year ->
            match season with
            | "winter" -> $"Winter {year}"
            | "spring" -> $"Spring {year}"
            | "summer" -> $"Summer {year}"
            | "fall" -> $"Fall {year}"
            | _ -> string year
      DnaGenresTitle = "Genres"
      DnaGenresNote = "By watch time. An anime counts in each of its genres."
      DnaOthers = "Others"
      TopAnimePrefix = "Top:"
      DnaFormatsTitle = "Formats"
      DnaEmptyTitle = "Not enough data yet"
      DnaEmptyCount = fun n -> $"""{n.ToString("N0", CultureInfo.GetCultureInfo "en")} of 15 fully watched episodes"""
      DnaEmptyBody =
        "The Anime DNA appears after 15 fully watched episodes. Sync more history with the Frunchy extension; MyAnimeList details follow shortly after."
      DnaEmptyAction = "How to sync"

      NavSignIn = "Sign in"
      NavAccount = "Account"
      NavSignOut = "Sign out"
      SkipToContent = "Skip to content"
      TooManyRequestsTitle = "Too many attempts"
      TooManyRequestsBody = "Please wait a few minutes and try again."
      FormExpiredTitle = "This form expired"
      FormExpiredBody = "Reload the page and try again."

      SignInTitle = "Sign in"
      SignInIntro = "Sign in or create your Frunchy account. Frunchy never asks for a password."
      SignInWith = fun provider -> $"Sign in with {provider}"
      SignInUnavailable = "Sign-in isn't available right now."
      SignInFailed = "Sign-in was cancelled or failed; try again."
      SignUpTitle = "Create your account"
      SignUpIntro = "First time here with this account. Enter your invite code and choose how you'll appear."
      InviteCodeLabel = "Invite code"
      UsernameLabel = "Username"
      UsernameHint = "Your public dashboard address: 3-30 characters, a-z 0-9 _ -, ending in a letter, digit or _. It can't be changed later."
      DisplayNameLabel = "Display name"
      CreateAccountButton = "Create account"
      HaveAccountLink = "I already have a Frunchy account"
      InviteInvalid = "This invite code isn't valid."
      UsernameInvalid = "That username isn't allowed: use 3-30 characters (a-z 0-9 _ -), starting with a letter or digit."
      UsernameTaken = "That username is already taken."
      DisplayNameInvalid = "The display name must be 1-60 characters."
      IdentityAlreadyUsed = "This sign-in account is already linked to a Frunchy account. Sign in again."

      AccountTitle = "Your account"
      AccountUsername = "Username"
      AccountDisplayName = "Display name"
      AccountDashboard = "Public dashboard"
      AccountSyncKey = "Extension"
      AccountKeyActive = "A sync key is active."
      AccountKeyMissing = "No sync key yet: connect the extension to start syncing."
      AccountSignInMethods = "Sign-in methods"
      AccountBinding = "Crunchyroll history"
      AccountBound = "Bound to a Crunchyroll profile."
      AccountUnbound = "Not bound yet: your first sync binds it."
      ConnectExtension = "Connect extension"
      KeyConfirmTitle = "Connect the extension"
      KeyConfirmBody = "This creates a new sync key. Any previous key stops working immediately."
      KeyConfirmButton = "Create sync key"
      KeyShownTitle = "Your sync key"
      KeyShownBody = "Paste this key in the Frunchy extension's options."
      KeyShownOnce = "It's shown only once: Frunchy doesn't keep a copy."
      BackToAccount = "Back to your account"

      ClaimTitle = "I already have an account"
      ClaimIntro = "If your Frunchy account was created for you, paste its current sync key (from the extension's options) to attach this sign-in to it."
      SyncKeyLabel = "Sync key"
      ClaimButton = "Attach to my account"
      ClaimRefused = "We couldn't verify that key."
      BackToSignUp = "Create a new account instead"

      SaveButton = "Save"
      DisplayNameSaved = "Display name saved."
      BindingResetButton = "Reset Crunchyroll binding"
      BindingResetDone = "Binding reset: your next sync binds your Crunchyroll profile again. Your history is kept."
      LinkProvider = fun provider -> $"Link {provider}"
      UnlinkButton = "Remove"
      IdentityLinkedNotice = "Sign-in method linked."
      IdentityUnlinkedNotice = "Sign-in method removed."
      LinkTaken = "That sign-in account is already linked to another Frunchy account."
      LinkDuplicate = "You already have a sign-in method from that provider."
      UnlinkLast = "You can't remove your last sign-in method."
      ReconfirmFailed = "We couldn't confirm it's you. Try again with the sign-in method you used."
      RecentSyncsTitle = "Recent syncs"
      NoSyncsYet = "No syncs yet: connect the extension and open your Crunchyroll history."
      SyncStatus =
        fun status ->
            match status with
            | "pending" -> "Pending"
            | "processing" -> "Processing"
            | "completed" -> "Completed"
            | "failed" -> "Failed"
            | other -> other
      SyncCounts = fun processed unresolved unsupported failed -> $"{processed} processed · {unresolved} unresolved · {unsupported} unsupported · {failed} failed"
      SecurityEventsTitle = "Security activity"
      SecurityEventLabel =
        fun event ->
            match event with
            | "account_created" -> "Account created"
            | "signed_in" -> "Signed in"
            | "claimed" -> "Account claimed"
            | "claim_refused" -> "Someone tried to claim this account with its sync key"
            | "identity_linked" -> "Sign-in method linked"
            | "identity_unlinked" -> "Sign-in method removed"
            | "sync_key_generated" -> "New sync key"
            | "binding_reset" -> "Crunchyroll binding reset"
            | "reconfirmed" -> "Identity confirmed"
            | "display_name_changed" -> "Display name changed"
            | "mal_set" -> "MyAnimeList match set by hand"
            | "mal_none" -> "Season marked as not on MyAnimeList"
            | "mal_retry" -> "MyAnimeList match retried"
            | "mal_auto" -> "MyAnimeList match sent back to automatic"
            | "operator_added" -> "Operator added"
            | "operator_removed" -> "Operator removed"
            | "comment_hidden" -> "Comment hidden"
            | "comment_unhidden" -> "Comment shown again"
            | other -> other
      ByOperator = "by the operator"

      DeleteAccountLink = "Delete my account"
      DeleteTitle = "Delete your account"
      DeleteWarning = "This deletes your account and everything Frunchy holds about you. It can't be undone."
      DeleteTypeUsername = fun username -> $"Type your username ({username}) to confirm"
      DeleteButton = "Delete my account forever"
      DeleteMismatch = "That username doesn't match your account."
      AccountDeletedNotice = "Your account and all your data have been deleted."

      // T017
      HomeNoPassword = "Frunchy never asks for your Crunchyroll password: it only reads the history pages the extension sends."
      // T019
      AccountProfileTitle = "Profile"
      AccountUsernameLocked = "Usernames can't be changed."
      CopyKey = "Copy key"
      KeyCopied = "Key copied"
      DeleteItems =
        [ "Your synced watch history, including the original submissions"
          "Your public dashboard and Anime DNA"
          "Your sign-in methods and sync key"
          "Your security activity" ]
      // T016
      NotOnMyAnimeList = "Not on MyAnimeList yet"
      WatchedPercent = fun p -> $"{p}%% watched"
      // T018
      UsernamePreviewPlaceholder = "your-username"
      DisplayNameHint = "How your name appears on your dashboard. You can change it later in your account."

      // Year in review (feature 005)
      YearTitle = fun y -> $"{y} in review"
      YearHeading = fun name y -> $"{name}'s {y} in anime"
      YearIntro = "One calendar year of this profile's Crunchyroll history, with MyAnimeList details."
      YearLink = "Year in review"
      YearRewatchNote = "Frunchy keeps the latest watch of each episode, so a rewatch counts in the year it was last watched. Years are in UTC."
      YearPickerLabel = "Years"
      YearHoursCaption = fun d -> $"""{d.ToString("N1", CultureInfo.GetCultureInfo "en")} days"""
      YearStatSeries = "Series"
      YearTopSeriesTitle = "Most watched series"
      YearTopSeriesEpisodes = fun n -> if n = 1L then "1 episode watched this year" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "en")} episodes watched this year"""
      YearMonthsTitle = "Month by month"
      YearBusiestMonth = fun month h -> $"""Busiest month: {month}, {h.ToString("N1", CultureInfo.GetCultureInfo "en")} hours"""
      YearMonthsChart = fun y -> $"Hours watched each month of {y}"
      YearSplitTitle = "New and continued"
      YearNewSeries = fun n -> if n = 1L then "1 new series" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "en")} new series"""
      YearContinuedSeries = fun n -> if n = 1L then "1 series continued from earlier years" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "en")} series continued from earlier years"""
      YearStreakTitle = "Longest streak"
      YearStreakDays = fun n -> if n = 1 then "1 day in a row" else $"{n} days in a row"
      YearStreakRange = fun from to' -> $"{from} to {to'}"
      YearStreakNone = "No fully watched episode this year."
      YearGenresTitle = "Genres"
      YearStudiosTitle = "Studios"
      YearFormatsTitle = "Formats"
      YearEmptyTitle = fun y -> $"Not enough data for {y} yet"
      YearEmptyCount = fun n -> $"""{n.ToString("N0", CultureInfo.GetCultureInfo "en")} of 15 fully watched episodes"""
      YearEmptyBody = "A year's review appears after 15 fully watched episodes in it. Pick another year, or sync more history with the Frunchy extension."

      // Anime page (feature 006)
      AnimeSeasonsTitle = "Seasons"
      AnimeSeasonNumber = fun n -> $"Season {n}"
      AnimeRewatchNote = "Frunchy keeps only the latest watch of each episode, so rewatches aren't counted."
      AnimeFirstWatch = "First watch"
      AnimeLatestWatch = "Latest watch"
      AnimeWatchOnCrunchyroll = "Watch on Crunchyroll"
      AnimeKnownEpisodes =
        fun watched known ->
            $"""{watched.ToString("N0", CultureInfo.GetCultureInfo "en")} of {known.ToString("N0", CultureInfo.GetCultureInfo "en")} known episodes"""
      AnimeOtherEpisodes = "Other episodes"
      AnimeDub = "Dub"
      AnimeStudio = fun name -> $"Studio: {name}"
      AnimeMalEpisodes = fun n -> if n = 1 then "1 episode" else $"{n} episodes"
      AnimeStatus =
        fun status ->
            match status with
            | "Finished Airing" -> "Finished airing"
            | "Currently Airing" -> "Airing now"
            | "Not yet aired" -> "Not yet aired"
            | other -> other
      AnimeEpisodeWatched = "Watched"
      AudioLanguage =
        fun code ->
            match code with
            | "ja-JP" -> "Japanese"
            | "en-US" -> "English"
            | "es-419" -> "Spanish (Latin America)"
            | "es-ES" -> "Spanish (Spain)"
            | "zh-CN" -> "Chinese"
            | "fr-FR" -> "French"
            | "pt-BR" -> "Portuguese (Brazil)"
            | "de-DE" -> "German"
            | "ru-RU" -> "Russian"
            | "it-IT" -> "Italian"
            | "ar-SA" -> "Arabic"
            | "hi-IN" -> "Hindi"
            | "ko-KR" -> "Korean"
            | other -> other
      // MyAnimeList review (/review/mal, feature 007)
      ReviewTitle = "Review MyAnimeList matches"
      ReviewIntro = "Seasons the matcher couldn't decide. Pick the right MyAnimeList entry, mark the season as not listed, or ask for another automatic try."
      ReviewManualTitle = "Manual MyAnimeList decisions"
      ReviewManualIntro = "Seasons decided by hand are never matched automatically. Send one back to automatic to let the matcher decide again."
      ReviewTabOpen = "To review"
      ReviewTabManual = "Manual decisions"
      ReviewFilterAll = "All"
      ReviewFilterAmbiguous = "Ambiguous"
      ReviewFilterNoMatch = "No match"
      ReviewFilterFailed = "Failed"
      ReviewColSeason = "Season"
      ReviewColStatus = "Status"
      ReviewColUsers = "Users"
      ReviewColFullyWatched = "Fully watched"
      ReviewColCandidates = "Candidates"
      ReviewColDecision = "Decision"
      ReviewColDecidedAt = "Decided"
      ReviewUseThis = "Use this"
      ReviewSetButton = "Set match"
      ReviewNoneButton = "Not on MyAnimeList"
      ReviewRetryButton = "Try again"
      ReviewAutoButton = "Back to automatic"
      ReviewMalIdLabel = "MyAnimeList id or link"
      ReviewMalIdHint = "A number such as 1535, or a myanimelist.net/anime link."
      ReviewNoCandidates = "No candidates stored."
      ReviewManualNone = "Not on MyAnimeList"
      ReviewEmpty = "Nothing to review. Every season is matched or waiting for the matcher."
      ReviewManualEmpty = "No manual decisions."
      ReviewMore = fun n -> $"{n} more not shown."
      ReviewAccountLink = "Review MyAnimeList matches"
      ReviewNotice =
        fun key ->
            match key with
            | "set" -> "Match saved."
            | "none" -> "Marked as not on MyAnimeList."
            | "retry" -> "Queued for another automatic try."
            | "auto" -> "Sent back to automatic matching."
            | "unknown-season" -> "That season doesn't exist. Nothing changed."
            | "unknown-anime" -> "MyAnimeList has no anime with that id. Nothing changed."
            | "unavailable" -> "MyAnimeList isn't available right now. Nothing changed, try again later."
            | "not-open" -> "That season was decided in the meantime. Nothing changed."
            | "bad-input" -> "Enter a MyAnimeList id or an anime link. Nothing changed."
            | other -> other

      OperatorsTitle = "Operators"
      OperatorsIntro = "Operators can review MyAnimeList matches. Only the root operator adds or removes them."
      OperatorsRootLabel = "Root operator"
      OperatorsRootNote = "Set in the server configuration, so it can't be added or removed here."
      OperatorsColUser = "User"
      OperatorsColAddedBy = "Added by"
      OperatorsColAddedAt = "Added"
      OperatorsAddedFromServer = "Server or deleted account"
      OperatorsAddLabel = "Username"
      OperatorsAddHint = "The username of an existing account."
      OperatorsAddButton = "Make operator"
      OperatorsRemoveButton = "Remove"
      OperatorsRemoveLabel = fun username -> $"Remove {username} as operator"
      OperatorsEmpty = "No operators besides the root."
      OperatorNotice =
        fun key ->
            match key with
            | "added" -> "Operator added."
            | "removed" -> "Operator removed."
            | "unknown-user" -> "There's no account with that username. Nothing changed."
            | "is-root" -> "That's the root operator, set in the server configuration. Nothing changed."
            | "already-operator" -> "That user is already an operator. Nothing changed."
            | "not-operator" -> "That user isn't an operator. Nothing changed."
            | "bad-input" -> "Enter a username. Nothing changed."
            | other -> other
      OperatorStatus = "You're an operator."
      OperatorsAccountLink = "Manage operators"

      EpisodePageTitle = fun series label -> $"{series}, {label}"
      EpisodeWatchOnCrunchyroll = "Watch on Crunchyroll"
      EpisodeSeriesOnCrunchyroll = "Series on Crunchyroll"
      CommentsHeading = "Comments"
      CommentsEmpty = "No comments yet. Be the first to write one."
      CommentEdited = "edited"
      CommentHiddenPlaceholder = "Hidden by an operator"
      CommentModerationNote = "Hiding a comment hides it on Frunchy only."
      CommentShowSpoiler = "Show spoiler"
      CommentFormLabel = "Your comment"
      CommentSpoilerLabel = "Contains spoilers"
      CommentSubmit = "Post comment"
      CommentSignInPrompt = "Sign in to write a comment."
      CommentError =
        function
        | "empty" -> "Write something before posting."
        | "too-long" -> "Comments can have up to 2,000 characters."
        | "bad-id" -> "This form is no longer valid. Reload the page and try again."
        | other -> other
      CommentEditTitle = "Edit your comment"
      CommentSave = "Save changes"
      CommentCancel = "Cancel"
      CommentDelete = "Delete comment"
      CommentDeleteConfirm = "Delete this comment? It can't be undone."
      CommentHide = "Hide"
      CommentUnhide = "Unhide"
      SpoilerSummary =
        fun season number label ->
            match number, label with
            | Some n, _ ->
                match season with
                | Some s -> $"Spoilers: S{s} E{n}"
                | None -> $"Spoilers: E{n}"
            | None, Some l -> $"Spoilers: {l}"
            | None, None -> "Spoilers: Special"

      CompatTitle = fun name -> $"You and {name}"
      CompatPageTitle = fun name -> $"Compare with {name}"
      CompatLevelName =
        function
        | 1 -> "Low"
        | 2 -> "Some"
        | 3 -> "Good"
        | 4 -> "High"
        | _ -> "Very high"
      CompatLevel =
        function
        | 1 -> "Low compatibility"
        | 2 -> "Some compatibility"
        | 3 -> "Good compatibility"
        | 4 -> "High compatibility"
        | _ -> "Very high compatibility"
      CompatMeterLabel =
        fun level ->
            let name =
                match level with
                | 1 -> "Low"
                | 2 -> "Some"
                | 3 -> "Good"
                | 4 -> "High"
                | _ -> "Very high"

            $"{name}: {level} of 5"
      CompatSharedCount =
        fun n ->
            if n = 0 then "No series in common yet"
            elif n = 1 then "1 series in common"
            else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "en")} series in common"""
      CompatSummary =
        fun name n genre ->
            let shared =
                if n = 0 then $"You and {name} share no series yet"
                elif n = 1 then $"You and {name} share 1 series"
                else $"""You and {name} share {n.ToString("N0", CultureInfo.GetCultureInfo "en")} series"""

            match genre with
            | Some g -> $"{shared} and a soft spot for {g}"
            | None -> shared
      CompatExplain = "This rests on the series you've both watched and how alike your time per genre is."
      CompatNoGenres = "One of you has no MyAnimeList genres yet, so this rests on the series you've both watched alone."
      CompatYou = "You"
      CompatSeriesCount =
        fun n -> if n = 1 then "1 series" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "en")} series"""
      CompatTimeTitle = "Where your time goes"
      CompatSharedTitle = "Series you've both watched"
      CompatTheirsTitle = fun name -> $"In {name}'s history, not yours"
      CompatStudiosTitle = "Studios you share"
      CompatMore = fun n -> $"""and {n.ToString("N0", CultureInfo.GetCultureInfo "en")} more"""
      CompatCoverage =
        fun name p -> $"""Based on {p.ToString("0.#", CultureInfo.GetCultureInfo "en")}%% of {name}'s watch time."""
      CompatNotEnoughViewer = "Your history isn't long enough yet. Compatibility appears from 15 fully watched episodes."
      CompatNotEnoughOwner =
        fun name -> $"{name}'s history isn't long enough yet. Compatibility appears from 15 fully watched episodes."
      CompatNotEnoughBoth =
        fun name ->
            $"Neither your history nor {name}'s is long enough yet. Compatibility appears from 15 fully watched episodes each."
      CompatSeeComparison = "See how you compare"

      NavCharts = "Charts"
      ChartsTitle = "Charts"
      ChartsTabWeek = "This week"
      ChartsTabSeason = "This season"
      ChartsTabAll = "All time"
      ChartsPeopleEpisodes =
        fun people episodes ->
            let n (x: int) = x.ToString("N0", CultureInfo.GetCultureInfo "en")
            let p = if people = 1 then "1 person" else $"{n people} people"
            let e = if episodes = 1 then "1 episode" else $"{n episodes} episodes"
            $"{p} · {e}"
      ChartsMoveUp = fun n -> $"Up {n}"
      ChartsMoveDown = fun n -> $"Down {n}"
      ChartsMoveSame = "Same"
      ChartsMoveNew = "New"
      ChartsTrendLabel = fun values -> "Last 8 weeks: " + System.String.Join(", ", values)
      ChartsLatestNote = "Each episode counts once, in the period of its latest watch."
      ChartsMinNote =
        fun n -> $"Series appear once {n} people have watched them."
      ChartsEmpty = "No series on this chart yet. Series appear when enough people have watched them."
      ChartsWatchedBy =
        fun n -> if n = 1 then "Watched by 1 person on Frunchy" else $"""Watched by {n.ToString("N0", CultureInfo.GetCultureInfo "en")} people on Frunchy"""
      SeriesPageTitle = fun title -> title
      SeriesKnownEpisodes =
        fun n -> if n = 1 then "1 known episode" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "en")} known episodes"""
      SeriesWatchedWeek =
        fun n -> if n = 1 then "Watched by 1 person this week" else $"""Watched by {n.ToString("N0", CultureInfo.GetCultureInfo "en")} people this week"""
      SeriesKnownNote = "These are the episodes Frunchy knows from people's histories, not the full Crunchyroll list."
      ChartsYourHistory = "Your history"
      SeriesPageLink = "About this series"
      SeriesCommentsLabel = fun n -> if n = 1 then "1 comment" else $"{n} comments" }

let es =
    { Lang = "es"
      Culture = CultureInfo.GetCultureInfo "es"
      SiteName = "Frunchy"
      Footer = "Frunchy, el Last.fm del anime"

      HomeTitle = "Inicio"
      HomeHeadline = "Tu identidad anime, en números"
      HomeIntro = "Frunchy convierte tu historial de Crunchyroll en estadísticas que puedes compartir."
      HomeHowToSyncTitle = "Cómo sincronizar"
      HomeSteps =
        [ "Instala la extensión de Frunchy y pega tu clave de sincronización en sus opciones."
          "Abre tu historial en crunchyroll.com."
          "La extensión lo envía a Frunchy y tu dashboard público se actualiza en unos instantes." ]
      HomeProfilesNote = "Los dashboards son públicos: cualquiera con el enlace de un perfil puede ver sus números."

      NotFoundTitle = "Página no encontrada"
      NotFoundBody = "No hay nada en esta dirección."
      ErrorTitle = "Algo salió mal"
      ErrorBody = "No pudimos completar tu solicitud. Inténtalo de nuevo más tarde."
      BackHome = "Volver al inicio"

      PublicProfileNote = "Este perfil es público: cualquiera con el enlace puede ver estos números."
      LastSync = "Última sincronización"
      NeverSynced = "Aún sin sincronizar"
      NewestEpisode = "Último episodio"
      SyncInProgress = "Se está procesando una sincronización; los números se actualizan al terminar."
      StatSeries = "Series"
      StatEpisodes = "Episodios vistos"
      StatHours = "Horas vistas"
      StatDays = "Días de vida"
      RecentTitle = "Últimos vistos"
      EpisodeLabel = fun n -> $"Episodio {n}"
      SpecialLabel = "Especial"
      Minutes = fun m -> $"{m} min"
      InProgress = "En curso"
      TopGenresTitle = "Géneros principales"
      TopGenresEpisodes =
        fun n -> if n = 1L then "1 episodio" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "es")} episodios"""
      TopGenresCoverage =
        fun p -> $"Basado en el {p}%% de los episodios vistos completos. Un episodio cuenta en cada uno de sus géneros."
      MyAnimeListLink = "MyAnimeList"
      ScoreLabel = "Puntuación en MyAnimeList"
      EmptyTitle = "Todavía no hay historial"
      EmptyBody =
        "Instala la extensión de Frunchy, pega tu clave de sincronización en sus opciones y abre tu historial de Crunchyroll: tus números aparecerán aquí en unos instantes."
      UserNotFoundTitle = "Usuario no encontrado"
      UserNotFoundBody = "No hay ningún perfil de Frunchy con este nombre."

      DnaTitle = "ADN anime"
      DnaHeading = fun name -> $"El ADN anime de {name}"
      DnaIntro = "Lo que ve este perfil, según su historial de Crunchyroll y MyAnimeList."
      DnaLink = "ADN anime"
      DashboardLink = "Dashboard"
      CopyLink = "Copiar enlace"
      LinkCopied = "Enlace copiado"
      CopyLinkFallback = "Copia esta dirección"
      DnaWatchTime = "Tiempo visto"
      DnaDaysOfLife = fun d -> $"""{d.ToString("N1", CultureInfo.GetCultureInfo "es")} días de vida"""
      DnaCoverageTile = "Cobertura de MyAnimeList"
      DnaCoverageCaption = "del tiempo visto tiene datos de MyAnimeList"
      DnaCoverage = fun p -> $"""Basado en el {p.ToString("0.#", CultureInfo.GetCultureInfo "es")}%% del tiempo visto."""
      DnaSectionNotEnough = "Aún no hay suficientes datos de MyAnimeList."
      DnaStudioTitle = "Estudio favorito"
      DnaRunnerUp = "Segundo"
      DnaDecadeTitle = "Década dominante"
      DnaDecade = fun y -> $"Años {y}"
      DnaDecadeCoverage = fun p -> $"""Basado en el {p.ToString("0.#", CultureInfo.GetCultureInfo "es")}%% del tiempo visto, la parte con año de estreno conocido."""
      DnaPaceTitle = "Ritmo, últimos 90 días"
      DnaPaceUnit = "episodios al día"
      DnaSteady = "Constante"
      DnaBursts = "A rachas"
      DnaPaceDetail = fun episodes weeks -> $"""{episodes.ToString("N0", CultureInfo.GetCultureInfo "es")} episodios, activo en {weeks} de 13 semanas"""
      DnaPaceChart = fun episodes -> $"""Episodios por día en los últimos 90 días, {episodes.ToString("N0", CultureInfo.GetCultureInfo "es")} en total"""
      DnaPaceNotEnough = "No hay suficientes datos recientes: menos de 10 episodios en los últimos 90 días."
      DnaInsigniaTitle = "Anime insignia"
      DnaInsigniaEpisodes =
        fun n total ->
            match total with
            | Some t -> $"""{n.ToString("N0", CultureInfo.GetCultureInfo "es")} de {t} episodios vistos"""
            | None -> if n = 1L then "1 episodio visto" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "es")} episodios vistos"""
      DnaInsigniaHours = fun h -> $"""{h.ToString("N1", CultureInfo.GetCultureInfo "es")} horas"""
      DnaSeason =
        fun season year ->
            match season with
            | "winter" -> $"Invierno {year}"
            | "spring" -> $"Primavera {year}"
            | "summer" -> $"Verano {year}"
            | "fall" -> $"Otoño {year}"
            | _ -> string year
      DnaGenresTitle = "Géneros"
      DnaGenresNote = "Por tiempo visto. Un anime cuenta en cada uno de sus géneros."
      DnaOthers = "Otros"
      TopAnimePrefix = "Destacado:"
      DnaFormatsTitle = "Formatos"
      DnaEmptyTitle = "Aún no hay suficientes datos"
      DnaEmptyCount = fun n -> $"""{n.ToString("N0", CultureInfo.GetCultureInfo "es")} de 15 episodios vistos completos"""
      DnaEmptyBody =
        "El ADN anime aparece tras 15 episodios vistos completos. Sincroniza más historial con la extensión de Frunchy; los datos de MyAnimeList llegan poco después."
      DnaEmptyAction = "Cómo sincronizar"

      NavSignIn = "Iniciar sesión"
      NavAccount = "Cuenta"
      NavSignOut = "Cerrar sesión"
      SkipToContent = "Saltar al contenido"
      TooManyRequestsTitle = "Demasiados intentos"
      TooManyRequestsBody = "Espera unos minutos y vuelve a intentarlo."
      FormExpiredTitle = "Este formulario caducó"
      FormExpiredBody = "Recarga la página y vuelve a intentarlo."

      SignInTitle = "Iniciar sesión"
      SignInIntro = "Inicia sesión o crea tu cuenta de Frunchy. Frunchy nunca te pide una contraseña."
      SignInWith = fun provider -> $"Iniciar sesión con {provider}"
      SignInUnavailable = "El inicio de sesión no está disponible ahora mismo."
      SignInFailed = "El inicio de sesión se canceló o falló; vuelve a intentarlo."
      SignUpTitle = "Crea tu cuenta"
      SignUpIntro = "Es la primera vez que entras con esta cuenta. Escribe tu código de invitación y elige cómo aparecerás."
      InviteCodeLabel = "Código de invitación"
      UsernameLabel = "Nombre de usuario"
      UsernameHint = "La dirección de tu dashboard público: 3-30 caracteres, a-z 0-9 _ -, terminando en letra, número o _. No se puede cambiar después."
      DisplayNameLabel = "Nombre visible"
      CreateAccountButton = "Crear cuenta"
      HaveAccountLink = "Ya tengo una cuenta de Frunchy"
      InviteInvalid = "Este código de invitación no es válido."
      UsernameInvalid = "Ese nombre de usuario no está permitido: usa 3-30 caracteres (a-z 0-9 _ -), empezando por una letra o un número."
      UsernameTaken = "Ese nombre de usuario ya está en uso."
      DisplayNameInvalid = "El nombre visible debe tener entre 1 y 60 caracteres."
      IdentityAlreadyUsed = "Esta cuenta de inicio de sesión ya está vinculada a una cuenta de Frunchy. Vuelve a iniciar sesión."

      AccountTitle = "Tu cuenta"
      AccountUsername = "Nombre de usuario"
      AccountDisplayName = "Nombre visible"
      AccountDashboard = "Dashboard público"
      AccountSyncKey = "Extensión"
      AccountKeyActive = "Hay una clave de sincronización activa."
      AccountKeyMissing = "Aún no hay clave de sincronización: conecta la extensión para empezar a sincronizar."
      AccountSignInMethods = "Métodos de inicio de sesión"
      AccountBinding = "Historial de Crunchyroll"
      AccountBound = "Vinculado a un perfil de Crunchyroll."
      AccountUnbound = "Aún sin vincular: tu primera sincronización lo vincula."
      ConnectExtension = "Conectar la extensión"
      KeyConfirmTitle = "Conecta la extensión"
      KeyConfirmBody = "Esto crea una nueva clave de sincronización. Cualquier clave anterior deja de funcionar al instante."
      KeyConfirmButton = "Crear clave de sincronización"
      KeyShownTitle = "Tu clave de sincronización"
      KeyShownBody = "Pega esta clave en las opciones de la extensión de Frunchy."
      KeyShownOnce = "Solo se muestra una vez: Frunchy no guarda una copia."
      BackToAccount = "Volver a tu cuenta"

      ClaimTitle = "Ya tengo una cuenta"
      ClaimIntro = "Si tu cuenta de Frunchy se creó por ti, pega su clave de sincronización actual (de las opciones de la extensión) para asociarle este inicio de sesión."
      SyncKeyLabel = "Clave de sincronización"
      ClaimButton = "Asociar a mi cuenta"
      ClaimRefused = "No pudimos verificar esa clave."
      BackToSignUp = "Crear una cuenta nueva"

      SaveButton = "Guardar"
      DisplayNameSaved = "Nombre visible guardado."
      BindingResetButton = "Restablecer el vínculo con Crunchyroll"
      BindingResetDone = "Vínculo restablecido: tu próxima sincronización vuelve a vincular tu perfil de Crunchyroll. Tu historial se conserva."
      LinkProvider = fun provider -> $"Vincular {provider}"
      UnlinkButton = "Quitar"
      IdentityLinkedNotice = "Método de inicio de sesión vinculado."
      IdentityUnlinkedNotice = "Método de inicio de sesión quitado."
      LinkTaken = "Esa cuenta de inicio de sesión ya está vinculada a otra cuenta de Frunchy."
      LinkDuplicate = "Ya tienes un método de inicio de sesión de ese proveedor."
      UnlinkLast = "No puedes quitar tu último método de inicio de sesión."
      ReconfirmFailed = "No pudimos confirmar que eres tú. Vuelve a intentarlo con el método de inicio de sesión que usaste."
      RecentSyncsTitle = "Sincronizaciones recientes"
      NoSyncsYet = "Aún no hay sincronizaciones: conecta la extensión y abre tu historial de Crunchyroll."
      SyncStatus =
        fun status ->
            match status with
            | "pending" -> "Pendiente"
            | "processing" -> "Procesando"
            | "completed" -> "Completada"
            | "failed" -> "Fallida"
            | other -> other
      SyncCounts = fun processed unresolved unsupported failed -> $"{processed} procesados · {unresolved} sin resolver · {unsupported} no compatibles · {failed} fallidos"
      SecurityEventsTitle = "Actividad de seguridad"
      SecurityEventLabel =
        fun event ->
            match event with
            | "account_created" -> "Cuenta creada"
            | "signed_in" -> "Inicio de sesión"
            | "claimed" -> "Cuenta reclamada"
            | "claim_refused" -> "Alguien intentó reclamar esta cuenta con su clave de sincronización"
            | "identity_linked" -> "Método de inicio de sesión vinculado"
            | "identity_unlinked" -> "Método de inicio de sesión quitado"
            | "sync_key_generated" -> "Nueva clave de sincronización"
            | "binding_reset" -> "Vínculo con Crunchyroll restablecido"
            | "reconfirmed" -> "Identidad confirmada"
            | "display_name_changed" -> "Nombre visible cambiado"
            | "mal_set" -> "Coincidencia de MyAnimeList fijada a mano"
            | "mal_none" -> "Temporada marcada como no disponible en MyAnimeList"
            | "mal_retry" -> "Coincidencia de MyAnimeList reintentada"
            | "mal_auto" -> "Coincidencia de MyAnimeList devuelta a automático"
            | "operator_added" -> "Operador añadido"
            | "operator_removed" -> "Operador quitado"
            | "comment_hidden" -> "Comentario oculto"
            | "comment_unhidden" -> "Comentario mostrado de nuevo"
            | other -> other
      ByOperator = "por el operador"

      DeleteAccountLink = "Eliminar mi cuenta"
      DeleteTitle = "Elimina tu cuenta"
      DeleteWarning = "Esto elimina tu cuenta y todo lo que Frunchy guarda sobre ti. No se puede deshacer."
      DeleteTypeUsername = fun username -> $"Escribe tu nombre de usuario ({username}) para confirmar"
      DeleteButton = "Eliminar mi cuenta para siempre"
      DeleteMismatch = "Ese nombre de usuario no coincide con tu cuenta."
      AccountDeletedNotice = "Tu cuenta y todos tus datos se han eliminado."

      // T017
      HomeNoPassword = "Frunchy nunca te pide la contraseña de Crunchyroll: solo lee las páginas del historial que envía la extensión."
      // T019
      AccountProfileTitle = "Perfil"
      AccountUsernameLocked = "El nombre de usuario no se puede cambiar."
      CopyKey = "Copiar clave"
      KeyCopied = "Clave copiada"
      DeleteItems =
        [ "Tu historial sincronizado, incluidos los envíos originales"
          "Tu dashboard público y tu ADN anime"
          "Tus métodos de inicio de sesión y tu clave de sincronización"
          "Tu actividad de seguridad" ]
      // T016
      NotOnMyAnimeList = "Aún no está en MyAnimeList"
      WatchedPercent = fun p -> $"{p}%% visto"
      // T018
      UsernamePreviewPlaceholder = "tu-usuario"
      DisplayNameHint = "Cómo aparece tu nombre en tu dashboard. Puedes cambiarlo después en tu cuenta."

      // Year in review (feature 005)
      YearTitle = fun y -> $"Resumen de {y}"
      YearHeading = fun name y -> $"El {y} anime de {name}"
      YearIntro = "Un año natural del historial de Crunchyroll de este perfil, con datos de MyAnimeList."
      YearLink = "Resumen del año"
      YearRewatchNote = "Frunchy guarda el último visionado de cada episodio, así que una revisión cuenta en el año en que se vio por última vez. Los años están en UTC."
      YearPickerLabel = "Años"
      YearHoursCaption = fun d -> $"""{d.ToString("N1", CultureInfo.GetCultureInfo "es")} días"""
      YearStatSeries = "Series"
      YearTopSeriesTitle = "Serie más vista"
      YearTopSeriesEpisodes = fun n -> if n = 1L then "1 episodio visto este año" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "es")} episodios vistos este año"""
      YearMonthsTitle = "Mes a mes"
      YearBusiestMonth = fun month h -> $"""Mes con más horas: {month}, {h.ToString("N1", CultureInfo.GetCultureInfo "es")} horas"""
      YearMonthsChart = fun y -> $"Horas vistas cada mes de {y}"
      YearSplitTitle = "Nuevas y continuadas"
      YearNewSeries = fun n -> if n = 1L then "1 serie nueva" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "es")} series nuevas"""
      YearContinuedSeries = fun n -> if n = 1L then "1 serie continuada de años anteriores" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "es")} series continuadas de años anteriores"""
      YearStreakTitle = "Racha más larga"
      YearStreakDays = fun n -> if n = 1 then "1 día seguido" else $"{n} días seguidos"
      YearStreakRange = fun from to' -> $"del {from} al {to'}"
      YearStreakNone = "Ningún episodio visto completo este año."
      YearGenresTitle = "Géneros"
      YearStudiosTitle = "Estudios"
      YearFormatsTitle = "Formatos"
      YearEmptyTitle = fun y -> $"Aún no hay suficientes datos de {y}"
      YearEmptyCount = fun n -> $"""{n.ToString("N0", CultureInfo.GetCultureInfo "es")} de 15 episodios vistos completos"""
      YearEmptyBody = "El resumen de un año aparece tras 15 episodios vistos completos en él. Elige otro año o sincroniza más historial con la extensión de Frunchy."

      // Anime page (feature 006)
      AnimeSeasonsTitle = "Temporadas"
      AnimeSeasonNumber = fun n -> $"Temporada {n}"
      AnimeRewatchNote = "Frunchy guarda solo el último visionado de cada episodio, así que las revisiones no se cuentan."
      AnimeFirstWatch = "Primer visionado"
      AnimeLatestWatch = "Último visionado"
      AnimeWatchOnCrunchyroll = "Ver en Crunchyroll"
      AnimeKnownEpisodes =
        fun watched known ->
            $"""{watched.ToString("N0", CultureInfo.GetCultureInfo "es")} de {known.ToString("N0", CultureInfo.GetCultureInfo "es")} episodios conocidos"""
      AnimeOtherEpisodes = "Otros episodios"
      AnimeDub = "Doblaje"
      AnimeStudio = fun name -> $"Estudio: {name}"
      AnimeMalEpisodes = fun n -> if n = 1 then "1 episodio" else $"{n} episodios"
      AnimeStatus =
        fun status ->
            match status with
            | "Finished Airing" -> "Emisión terminada"
            | "Currently Airing" -> "En emisión"
            | "Not yet aired" -> "Aún sin emitir"
            | other -> other
      AnimeEpisodeWatched = "Visto"
      AudioLanguage =
        fun code ->
            match code with
            | "ja-JP" -> "Japonés"
            | "en-US" -> "Inglés"
            | "es-419" -> "Español (Latinoamérica)"
            | "es-ES" -> "Español (España)"
            | "zh-CN" -> "Chino"
            | "fr-FR" -> "Francés"
            | "pt-BR" -> "Portugués (Brasil)"
            | "de-DE" -> "Alemán"
            | "ru-RU" -> "Ruso"
            | "it-IT" -> "Italiano"
            | "ar-SA" -> "Árabe"
            | "hi-IN" -> "Hindi"
            | "ko-KR" -> "Coreano"
            | other -> other
      // MyAnimeList review (/review/mal, feature 007)
      ReviewTitle = "Revisar coincidencias de MyAnimeList"
      ReviewIntro = "Temporadas que el emparejador no pudo decidir. Elige la entrada correcta de MyAnimeList, marca la temporada como no listada o pide otro intento automático."
      ReviewManualTitle = "Decisiones manuales de MyAnimeList"
      ReviewManualIntro = "Las temporadas decididas a mano nunca se emparejan automáticamente. Devuelve una a automático para que el emparejador decida de nuevo."
      ReviewTabOpen = "Por revisar"
      ReviewTabManual = "Decisiones manuales"
      ReviewFilterAll = "Todas"
      ReviewFilterAmbiguous = "Ambiguas"
      ReviewFilterNoMatch = "Sin coincidencia"
      ReviewFilterFailed = "Fallidas"
      ReviewColSeason = "Temporada"
      ReviewColStatus = "Estado"
      ReviewColUsers = "Usuarios"
      ReviewColFullyWatched = "Vistos completos"
      ReviewColCandidates = "Candidatas"
      ReviewColDecision = "Decisión"
      ReviewColDecidedAt = "Decidida"
      ReviewUseThis = "Usar esta"
      ReviewSetButton = "Fijar coincidencia"
      ReviewNoneButton = "No está en MyAnimeList"
      ReviewRetryButton = "Reintentar"
      ReviewAutoButton = "Volver a automático"
      ReviewMalIdLabel = "Id o enlace de MyAnimeList"
      ReviewMalIdHint = "Un número como 1535, o un enlace de myanimelist.net/anime."
      ReviewNoCandidates = "No hay candidatas guardadas."
      ReviewManualNone = "No está en MyAnimeList"
      ReviewEmpty = "Nada que revisar. Todas las temporadas están emparejadas o esperan al emparejador."
      ReviewManualEmpty = "No hay decisiones manuales."
      ReviewMore = fun n -> if n = 1 then "1 más sin mostrar." else $"{n} más sin mostrar."
      ReviewAccountLink = "Revisar coincidencias de MyAnimeList"
      ReviewNotice =
        fun key ->
            match key with
            | "set" -> "Coincidencia guardada."
            | "none" -> "Marcada como no disponible en MyAnimeList."
            | "retry" -> "En cola para otro intento automático."
            | "auto" -> "Devuelta al emparejamiento automático."
            | "unknown-season" -> "Esa temporada no existe. No se cambió nada."
            | "unknown-anime" -> "MyAnimeList no tiene ningún anime con ese id. No se cambió nada."
            | "unavailable" -> "MyAnimeList no está disponible ahora. No se cambió nada, inténtalo más tarde."
            | "not-open" -> "Esa temporada se decidió mientras tanto. No se cambió nada."
            | "bad-input" -> "Escribe un id de MyAnimeList o un enlace de anime. No se cambió nada."
            | other -> other

      OperatorsTitle = "Operadores"
      OperatorsIntro = "Los operadores pueden revisar las coincidencias de MyAnimeList. Solo el operador raíz los añade o los quita."
      OperatorsRootLabel = "Operador raíz"
      OperatorsRootNote = "Se define en la configuración del servidor, así que aquí no se puede añadir ni quitar."
      OperatorsColUser = "Usuario"
      OperatorsColAddedBy = "Añadido por"
      OperatorsColAddedAt = "Añadido"
      OperatorsAddedFromServer = "Servidor o cuenta eliminada"
      OperatorsAddLabel = "Nombre de usuario"
      OperatorsAddHint = "El nombre de usuario de una cuenta existente."
      OperatorsAddButton = "Hacer operador"
      OperatorsRemoveButton = "Quitar"
      OperatorsRemoveLabel = fun username -> $"Quitar a {username} como operador"
      OperatorsEmpty = "No hay operadores además del raíz."
      OperatorNotice =
        fun key ->
            match key with
            | "added" -> "Operador añadido."
            | "removed" -> "Operador quitado."
            | "unknown-user" -> "No hay ninguna cuenta con ese nombre de usuario. No se cambió nada."
            | "is-root" -> "Es el operador raíz, definido en la configuración del servidor. No se cambió nada."
            | "already-operator" -> "Ese usuario ya es operador. No se cambió nada."
            | "not-operator" -> "Ese usuario no es operador. No se cambió nada."
            | "bad-input" -> "Escribe un nombre de usuario. No se cambió nada."
            | other -> other
      OperatorStatus = "Eres operador."
      OperatorsAccountLink = "Gestionar operadores"

      EpisodePageTitle = fun series label -> $"{series}, {label}"
      EpisodeWatchOnCrunchyroll = "Ver en Crunchyroll"
      EpisodeSeriesOnCrunchyroll = "Serie en Crunchyroll"
      CommentsHeading = "Comentarios"
      CommentsEmpty = "Aún no hay comentarios. Escribe el primero."
      CommentEdited = "editado"
      CommentHiddenPlaceholder = "Oculto por un operador"
      CommentModerationNote = "Ocultar un comentario solo lo oculta en Frunchy."
      CommentShowSpoiler = "Mostrar spoiler"
      CommentFormLabel = "Tu comentario"
      CommentSpoilerLabel = "Contiene spoilers"
      CommentSubmit = "Publicar comentario"
      CommentSignInPrompt = "Inicia sesión para escribir un comentario."
      CommentError =
        function
        | "empty" -> "Escribe algo antes de publicar."
        | "too-long" -> "Los comentarios pueden tener hasta 2.000 caracteres."
        | "bad-id" -> "Este formulario ya no es válido. Recarga la página e inténtalo de nuevo."
        | other -> other
      CommentEditTitle = "Editar tu comentario"
      CommentSave = "Guardar cambios"
      CommentCancel = "Cancelar"
      CommentDelete = "Eliminar comentario"
      CommentDeleteConfirm = "¿Eliminar este comentario? No se puede deshacer."
      CommentHide = "Ocultar"
      CommentUnhide = "Mostrar de nuevo"
      SpoilerSummary =
        fun season number label ->
            match number, label with
            | Some n, _ ->
                match season with
                | Some s -> $"Spoilers: T{s} E{n}"
                | None -> $"Spoilers: E{n}"
            | None, Some l -> $"Spoilers: {l}"
            | None, None -> "Spoilers: Especial"

      CompatTitle = fun name -> $"Tú y {name}"
      CompatPageTitle = fun name -> $"Compararte con {name}"
      CompatLevelName =
        function
        | 1 -> "Baja"
        | 2 -> "Algo"
        | 3 -> "Buena"
        | 4 -> "Alta"
        | _ -> "Muy alta"
      CompatLevel =
        function
        | 1 -> "Compatibilidad baja"
        | 2 -> "Algo de compatibilidad"
        | 3 -> "Compatibilidad buena"
        | 4 -> "Compatibilidad alta"
        | _ -> "Compatibilidad muy alta"
      CompatMeterLabel =
        fun level ->
            let name =
                match level with
                | 1 -> "Baja"
                | 2 -> "Algo"
                | 3 -> "Buena"
                | 4 -> "Alta"
                | _ -> "Muy alta"

            $"{name}: {level} de 5"
      CompatSharedCount =
        fun n ->
            if n = 0 then "Todavía no hay series en común"
            elif n = 1 then "1 serie en común"
            else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "es")} series en común"""
      CompatSummary =
        fun name n genre ->
            let shared =
                if n = 0 then $"Tú y {name} todavía no comparten ninguna serie"
                elif n = 1 then $"Tú y {name} comparten 1 serie"
                else $"""Tú y {name} comparten {n.ToString("N0", CultureInfo.GetCultureInfo "es")} series"""

            match genre with
            | Some g -> $"{shared} y una debilidad por {g}"
            | None -> shared
      CompatExplain =
        "Se basa en las series que han visto los dos y en cuánto se parece el tiempo que dedican a cada género."
      CompatNoGenres =
        "Alguno de los dos todavía no tiene géneros de MyAnimeList, así que se basa solo en las series que han visto los dos."
      CompatYou = "Tú"
      CompatSeriesCount =
        fun n -> if n = 1 then "1 serie" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "es")} series"""
      CompatTimeTitle = "En qué se va tu tiempo"
      CompatSharedTitle = "Series que han visto los dos"
      CompatTheirsTitle = fun name -> $"En el historial de {name}, no en el tuyo"
      CompatStudiosTitle = "Estudios en común"
      CompatMore = fun n -> $"""y {n.ToString("N0", CultureInfo.GetCultureInfo "es")} más"""
      CompatCoverage =
        fun name p -> $"""Basado en el {p.ToString("0.#", CultureInfo.GetCultureInfo "es")}%% del tiempo visto de {name}."""
      CompatNotEnoughViewer =
        "Tu historial todavía no es lo bastante largo. La compatibilidad aparece desde 15 episodios vistos completos."
      CompatNotEnoughOwner =
        fun name ->
            $"El historial de {name} todavía no es lo bastante largo. La compatibilidad aparece desde 15 episodios vistos completos."
      CompatNotEnoughBoth =
        fun name ->
            $"Ni tu historial ni el de {name} son todavía lo bastante largos. La compatibilidad aparece desde 15 episodios vistos completos en cada uno."
      CompatSeeComparison = "Ver cómo se comparan"

      NavCharts = "Rankings"
      ChartsTitle = "Rankings"
      ChartsTabWeek = "Esta semana"
      ChartsTabSeason = "Esta temporada"
      ChartsTabAll = "Histórico"
      ChartsPeopleEpisodes =
        fun people episodes ->
            let n (x: int) = x.ToString("N0", CultureInfo.GetCultureInfo "es")
            let p = if people = 1 then "1 persona" else $"{n people} personas"
            let e = if episodes = 1 then "1 episodio" else $"{n episodes} episodios"
            $"{p} · {e}"
      ChartsMoveUp = fun n -> $"Sube {n}"
      ChartsMoveDown = fun n -> $"Baja {n}"
      ChartsMoveSame = "Igual"
      ChartsMoveNew = "Nuevo"
      ChartsTrendLabel = fun values -> "Últimas 8 semanas: " + System.String.Join(", ", values)
      ChartsLatestNote = "Cada episodio cuenta una vez, en el periodo de su última vez visto."
      ChartsMinNote =
        fun n -> $"Las series aparecen cuando las han visto al menos {n} personas."
      ChartsEmpty = "Todavía no hay series en este ranking. Las series aparecen cuando las ven suficientes personas."
      ChartsWatchedBy =
        fun n -> if n = 1 then "Visto por 1 persona en Frunchy" else $"""Visto por {n.ToString("N0", CultureInfo.GetCultureInfo "es")} personas en Frunchy"""
      SeriesPageTitle = fun title -> title
      SeriesKnownEpisodes =
        fun n -> if n = 1 then "1 episodio conocido" else $"""{n.ToString("N0", CultureInfo.GetCultureInfo "es")} episodios conocidos"""
      SeriesWatchedWeek =
        fun n -> if n = 1 then "Visto por 1 persona esta semana" else $"""Visto por {n.ToString("N0", CultureInfo.GetCultureInfo "es")} personas esta semana"""
      SeriesKnownNote = "Son los episodios que Frunchy conoce por los historiales de la gente, no la lista completa de Crunchyroll."
      ChartsYourHistory = "Tu historial"
      SeriesPageLink = "Sobre esta serie"
      SeriesCommentsLabel = fun n -> if n = 1 then "1 comentario" else $"{n} comentarios" }

/// Spanish when the request culture is Spanish (any region), English otherwise
let forCulture (culture: CultureInfo) =
    if culture.TwoLetterISOLanguageName = "es" then es else en

/// Texts for the current request (RequestLocalization sets CurrentUICulture per request)
let current () = forCulture CultureInfo.CurrentUICulture
