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
      EmptyTitle: string
      EmptyBody: string
      UserNotFoundTitle: string
      UserNotFoundBody: string

      // Navigation and generic web errors (feature 002)
      NavSignIn: string
      NavAccount: string
      NavSignOut: string
      TooManyRequestsTitle: string
      TooManyRequestsBody: string
      FormExpiredTitle: string
      FormExpiredBody: string }

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
      EmptyTitle = "No history yet"
      EmptyBody =
        "Install the Frunchy extension, paste your sync key in its options and open your Crunchyroll history: your numbers appear here a moment later."
      UserNotFoundTitle = "User not found"
      UserNotFoundBody = "There's no Frunchy profile with this name."

      NavSignIn = "Sign in"
      NavAccount = "Account"
      NavSignOut = "Sign out"
      TooManyRequestsTitle = "Too many attempts"
      TooManyRequestsBody = "Please wait a few minutes and try again."
      FormExpiredTitle = "This form expired"
      FormExpiredBody = "Reload the page and try again." }

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
      EmptyTitle = "Todavía no hay historial"
      EmptyBody =
        "Instala la extensión de Frunchy, pega tu clave de sincronización en sus opciones y abre tu historial de Crunchyroll: tus números aparecerán aquí en unos instantes."
      UserNotFoundTitle = "Usuario no encontrado"
      UserNotFoundBody = "No hay ningún perfil de Frunchy con este nombre."

      NavSignIn = "Iniciar sesión"
      NavAccount = "Cuenta"
      NavSignOut = "Cerrar sesión"
      TooManyRequestsTitle = "Demasiados intentos"
      TooManyRequestsBody = "Espera unos minutos y vuelve a intentarlo."
      FormExpiredTitle = "Este formulario caducó"
      FormExpiredBody = "Recarga la página y vuelve a intentarlo." }

/// Spanish when the request culture is Spanish (any region), English otherwise
let forCulture (culture: CultureInfo) =
    if culture.TwoLetterISOLanguageName = "es" then es else en

/// Texts for the current request (RequestLocalization sets CurrentUICulture per request)
let current () = forCulture CultureInfo.CurrentUICulture
