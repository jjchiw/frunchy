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
      DnaGenresTitle: string
      DnaGenresNote: string
      DnaOthers: string
      DnaTopAnime: string -> string
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
      // T018
      /// Stands in for the username in the sign-up page's address preview while the field is empty
      UsernamePreviewPlaceholder: string
      DisplayNameHint: string }

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
      DnaGenresTitle = "Genres"
      DnaGenresNote = "By watch time. An anime counts in each of its genres."
      DnaOthers = "Others"
      DnaTopAnime = fun title -> $"Top: {title}"
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
      UsernameHint = "Your public dashboard address: 3-30 characters, a-z 0-9 _ -. It can't be changed later."
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
            | other -> other
      ByOperator = "by the operator"

      DeleteAccountLink = "Delete my account"
      DeleteTitle = "Delete your account"
      DeleteWarning = "This deletes your account and everything Frunchy holds about you: your synced history (including the original submissions), your dashboard, sign-in methods and security activity. It can't be undone."
      DeleteTypeUsername = fun username -> $"Type your username ({username}) to confirm"
      DeleteButton = "Delete my account forever"
      DeleteMismatch = "That username doesn't match your account."
      AccountDeletedNotice = "Your account and all your data have been deleted."
      // T018
      UsernamePreviewPlaceholder = "your-username"
      DisplayNameHint = "How your name appears on your dashboard. You can change it later in your account." }

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
      DashboardLink = "Panel"
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
      DnaGenresTitle = "Géneros"
      DnaGenresNote = "Por tiempo visto. Un anime cuenta en cada uno de sus géneros."
      DnaOthers = "Otros"
      DnaTopAnime = fun title -> $"Destacado: {title}"
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
      UsernameHint = "La dirección de tu dashboard público: 3-30 caracteres, a-z 0-9 _ -. No se puede cambiar después."
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
            | other -> other
      ByOperator = "por el operador"

      DeleteAccountLink = "Eliminar mi cuenta"
      DeleteTitle = "Elimina tu cuenta"
      DeleteWarning = "Esto elimina tu cuenta y todo lo que Frunchy guarda sobre ti: tu historial sincronizado (incluidos los envíos originales), tu dashboard, tus métodos de inicio de sesión y tu actividad de seguridad. No se puede deshacer."
      DeleteTypeUsername = fun username -> $"Escribe tu nombre de usuario ({username}) para confirmar"
      DeleteButton = "Eliminar mi cuenta para siempre"
      DeleteMismatch = "Ese nombre de usuario no coincide con tu cuenta."
      AccountDeletedNotice = "Tu cuenta y todos tus datos se han eliminado."
      // T018
      UsernamePreviewPlaceholder = "tu-usuario"
      DisplayNameHint = "Cómo aparece tu nombre en tu dashboard. Puedes cambiarlo después en tu cuenta." }

/// Spanish when the request culture is Spanish (any region), English otherwise
let forCulture (culture: CultureInfo) =
    if culture.TwoLetterISOLanguageName = "es" then es else en

/// Texts for the current request (RequestLocalization sets CurrentUICulture per request)
let current () = forCulture CultureInfo.CurrentUICulture
