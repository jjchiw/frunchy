/// A cookie-keeping, non-redirecting HTTP client over the TestServer, with antiforgery-aware form posts
module FrunchyBackend.Tests.Infrastructure.Browser

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks

type private CookieHandler(inner: HttpMessageHandler, jar: CookieContainer) =
    inherit DelegatingHandler(inner)

    override _.SendAsync(request, cancellationToken) =
        let header = jar.GetCookieHeader request.RequestUri

        if header <> "" then
            request.Headers.Add("Cookie", header)

        // protected members can't be used inside the task closure, so start the send first
        let sending = base.SendAsync(request, cancellationToken)

        task {
            let! (response: HttpResponseMessage) = sending

            match response.Headers.TryGetValues "Set-Cookie" with
            | true, values ->
                for value in values do
                    jar.SetCookies(request.RequestUri, value)
            | _ -> ()

            return response
        }

type Page =
    { Status: HttpStatusCode
      /// Final path after following redirects (path + query)
      Path: string
      Html: string
      Location: string option }

type Browser(handler: HttpMessageHandler, language: string) =
    let jar = CookieContainer()
    let client = new HttpClient(new CookieHandler(handler, jar), BaseAddress = Uri "http://localhost")

    let send (request: HttpRequestMessage) =
        request.Headers.AcceptLanguage.ParseAdd language
        client.SendAsync request

    member _.Cookies = jar

    member _.CookieNames =
        [ for c in jar.GetAllCookies() -> c.Name ]

    /// One request, no redirect following
    member _.Send(request: HttpRequestMessage) : Task<HttpResponseMessage> = send request

    /// GET following redirects (max 10); returns the final page
    member this.Get(path: string) : Task<Page> =
        task {
            let mutable current = path
            let mutable result = None
            let mutable hops = 0

            while result.IsNone do
                let! response = send (new HttpRequestMessage(HttpMethod.Get, current))
                let! body = response.Content.ReadAsStringAsync()

                match response.Headers.Location with
                | null ->
                    result <-
                        Some
                            { Status = response.StatusCode
                              Path = current
                              Html = WebUtility.HtmlDecode body
                              Location = None }
                | location when hops < 10 ->
                    hops <- hops + 1
                    current <- (if location.IsAbsoluteUri then location.PathAndQuery else string location)
                | location ->
                    result <-
                        Some
                            { Status = response.StatusCode
                              Path = current
                              Html = body
                              Location = Some(string location) }

            return result.Value
        }

    /// GET without following redirects
    member _.GetRaw(path: string) : Task<HttpResponseMessage> = send (new HttpRequestMessage(HttpMethod.Get, path))

    /// Load `formPage`, take its antiforgery token, POST `fields` to `action` (no redirect following)
    member this.PostForm(formPage: string, action: string, fields: (string * string) list) : Task<HttpResponseMessage> =
        task {
            let! page = this.Get formPage
            let token = Regex.Match(page.Html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")

            let tokenField =
                if token.Success then [ "__RequestVerificationToken", token.Groups[1].Value ] else []

            let content = new FormUrlEncodedContent(tokenField @ fields |> List.map KeyValuePair)
            return! send (new HttpRequestMessage(HttpMethod.Post, action, Content = content))
        }

    /// POST without any antiforgery token (CSRF tests)
    member _.PostFormWithoutToken(action: string, fields: (string * string) list) : Task<HttpResponseMessage> =
        let content = new FormUrlEncodedContent(fields |> List.map KeyValuePair)
        send (new HttpRequestMessage(HttpMethod.Post, action, Content = content))

    /// Follow a redirect response to its final page
    member this.Follow(response: HttpResponseMessage) : Task<Page> =
        task {
            match response.Headers.Location with
            | null ->
                let! body = response.Content.ReadAsStringAsync()
                return { Status = response.StatusCode; Path = ""; Html = WebUtility.HtmlDecode body; Location = None }
            | location -> return! this.Get(if location.IsAbsoluteUri then location.PathAndQuery else string location)
        }

    interface IDisposable with
        member _.Dispose() = client.Dispose()
