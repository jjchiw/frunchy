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
      BackHome: string }

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
      BackHome = "Back to home" }

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
      BackHome = "Volver al inicio" }

/// Spanish when the request culture is Spanish (any region), English otherwise
let forCulture (culture: CultureInfo) =
    if culture.TwoLetterISOLanguageName = "es" then es else en

/// Texts for the current request (RequestLocalization sets CurrentUICulture per request)
let current () = forCulture CultureInfo.CurrentUICulture
