/// Feature 016 (US3): the data-protection keys live in Postgres, encrypted, so a restart or a second copy keeps
/// everyone signed in. Two app instances on one database stand for two copies.
module FrunchyBackend.Tests.DataProtectionTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.DataProtection.KeyManagement
open Microsoft.AspNetCore.DataProtection.Repositories
open Microsoft.AspNetCore.DataProtection.XmlEncryption
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Xunit
open FrunchyBackend
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private keyRows (db: Database.DbConfig) =
    Db.dump db "SELECT xml FROM data_protection_keys ORDER BY id"

let private tokenOf (html: string) =
    Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value

/// The browser's cookies handed to a browser on another copy, as if the same browser spoke to that copy
let private copyCookies (source: Browser) (target: Browser) =
    target.Cookies.Add(source.Cookies.GetAllCookies())

/// A person signs in on copy A and loads a form there; copy B must accept the session cookie and the form's
/// antiforgery token (a display-name change saved through B)
let private sessionAndFormWorkAcross (copyA: TestApp) (copyB: TestApp) (subject: string) (username: string) =
    task {
        use! onA = signedUp copyA subject username
        let! formPage = onA.Get "/account"
        let token = tokenOf formPage.Html
        Assert.NotEmpty token

        use onB = copyB.NewBrowser()
        copyCookies onA onB

        // The session
        let! page = onB.Get "/account"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.Equal("/account", page.Path)

        // The form token
        use content =
            new FormUrlEncodedContent([ "__RequestVerificationToken", token; "displayName", "Seen On B" ] |> List.map KeyValuePair)

        use request = new HttpRequestMessage(HttpMethod.Post, "/account/display-name", Content = content)
        let! response = onB.Send request
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)

        let! name = Db.scalar<string> copyA.Db "SELECT display_name FROM users WHERE username = @u" [ "u", box username ]
        Assert.Equal("Seen On B", name)
    }

/// Someone signs in on an app that is then stopped; their cookies are what a browser would keep
let private signedInOnAStoppedApp (db: Database.DbConfig) : Task<Net.CookieCollection> =
    task {
        use! app = createApp db
        use! browser = signedUp app "sub-1" "animenerd"
        return browser.Cookies.GetAllCookies()
    }

/// A base configuration for a build that is never started, so a startup check shows as an exception
let private build (environment: string) (settings: (string * string) list) =
    Program.buildApp Array.empty (fun builder ->
        builder.WebHost.UseTestServer() |> ignore
        builder.Environment.EnvironmentName <- environment

        builder.Configuration.AddInMemoryCollection(
            dict (
                [ "ConnectionStrings:Database", "Host=unused"
                  Program.WorkerEnabledKey, "false"
                  Program.EnrichmentWorkerEnabledKey, "false"
                  Program.FediverseWorkerEnabledKey, "false" ]
                @ settings
            )
        )
        |> ignore)

type private CapturedLogs() =
    let lines = ConcurrentQueue<string>()
    member _.Lines = List.ofSeq lines

    interface ILoggerProvider with
        member _.CreateLogger(_: string) =
            { new ILogger with
                member _.BeginScope<'S>(_: 'S) = Unchecked.defaultof<IDisposable>
                member _.IsEnabled(_: LogLevel) = true

                member _.Log<'S>(_: LogLevel, _: EventId, state: 'S, ex: exn, formatter: Func<'S, exn, string>) =
                    lines.Enqueue(formatter.Invoke(state, ex)) }

        member _.Dispose() = ()

/// Makes two copies read the (empty) key table before either stores a key, so both create their own
type private BarrierRepository(inner: IXmlRepository, barrier: Barrier) =
    let mutable first = true

    interface IXmlRepository with
        member _.GetAllElements() =
            let elements = inner.GetAllElements()

            if first then
                first <- false

                if not (barrier.SignalAndWait(TimeSpan.FromSeconds 15.0) ) then
                    failwith "the other copy never read the key table"

            elements

        member _.StoreElement(element: XElement, friendlyName: string) = inner.StoreElement(element, friendlyName)

// ---------------------------------------------------------------------------
// Restart and copies
// ---------------------------------------------------------------------------

[<Fact>]
let ``a second copy on the same database accepts the first copy's session and form token`` () =
    task {
        let! db = newDatabase ()
        use! first = createApp db
        use! second = createApp db

        do! sessionAndFormWorkAcross first second "sub-1" "animenerd"
        let! rows = keyRows db
        Assert.Equal(1, rows.Length)
    }

[<Fact>]
let ``a restarted app still accepts the session cookie it issued before`` () =
    task {
        let! db = newDatabase ()
        let! cookies = signedInOnAStoppedApp db

        use! after = createApp db
        use browser = after.NewBrowser()
        browser.Cookies.Add cookies
        let! page = browser.Get "/account"
        Assert.Equal("/account", page.Path)
        Assert.Equal(HttpStatusCode.OK, page.Status)
    }

// ---------------------------------------------------------------------------
// First-start race
// ---------------------------------------------------------------------------

[<Fact>]
let ``two copies started together on an empty table each make a key and still accept each other's cookies`` () =
    task {
        let! db = newDatabase ()
        let! before = Db.count db "data_protection_keys"
        Assert.Equal(0L, before)
        let barrier = new Barrier 2

        let start () =
            Task.Run<TestApp>(fun () ->
                createAppWith db [] (fun builder ->
                    builder.Services.PostConfigure<KeyManagementOptions>(fun options ->
                        options.XmlRepository <- BarrierRepository(options.XmlRepository, barrier))
                    |> ignore))

        let! apps = Task.WhenAll [ start (); start () ]
        use first = apps[0]
        use second = apps[1]

        // If the key ring loads lazily instead of at startup, load it on both copies together
        let! _ =
            Task.WhenAll
                [ for app in apps ->
                      Task.Run(fun () -> app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("race").Protect [| 1uy |] |> ignore) ]

        // Each copy created its own key (that is the race), and neither rejects the other's payloads
        let! rows = keyRows db
        Assert.Equal(2, rows.Length)

        do! sessionAndFormWorkAcross first second "sub-1" "animenerd"
        do! sessionAndFormWorkAcross second first "sub-2" "otherfan"
    }

// ---------------------------------------------------------------------------
// Encryption at rest
// ---------------------------------------------------------------------------

/// The key material the framework holds in memory (what a plain store would have written)
let private masterKeysInMemory (app: TestApp) =
    [ for key in app.Services.GetRequiredService<IKeyManager>().GetAllKeys() do
          let xml = key.Descriptor.ExportToXml().SerializedDescriptorElement
          for element in xml.Descendants() do
              if element.Name.LocalName = "masterKey" then
                  yield element.Element(XName.Get("value", element.Name.NamespaceName)).Value ]

[<Fact>]
let ``with the encryption key set a stored key holds an encrypted secret and no plain key material`` () =
    task {
        use! app = newApp ()
        let! rows = keyRows app.Db
        Assert.NotEmpty rows

        let secrets = masterKeysInMemory app
        Assert.NotEmpty secrets

        for row in rows do
            Assert.Contains("encryptedSecret", row)
            Assert.Contains("AES-256-GCM", row)
            Assert.DoesNotContain("masterKey", row)

            for secret in secrets do
                Assert.DoesNotContain(secret, row)
    }

// ---------------------------------------------------------------------------
// Startup refusals and settings
// ---------------------------------------------------------------------------

[<Fact>]
let ``Production refuses to start without an encryption key, naming the setting`` () =
    for missing in [ ""; "   " ] do
        let settings = if missing = "" then [ DataProtectionStore.EncryptionKeySetting, "" ] else [ DataProtectionStore.EncryptionKeySetting, missing ]
        let ex = Assert.Throws<InvalidOperationException>(fun () -> build "Production" settings |> ignore)
        Assert.Contains(DataProtectionStore.EncryptionKeySetting, ex.Message)

    // Not set at all
    let ex = Assert.Throws<InvalidOperationException>(fun () -> build "Production" [] |> ignore)
    Assert.Contains(DataProtectionStore.EncryptionKeySetting, ex.Message)

[<Fact>]
let ``a key that is not base64 of 32 bytes stops startup without echoing the value`` () =
    for environment in [ "Production"; "Development" ] do
        for bad in [ "this-is-not-base64-at-all!!"; "AAAAAAAAAAAAAAAAAAAAAA==" (* 16 bytes *); "c2hvcnQ=" ] do
            let ex =
                Assert.Throws<InvalidOperationException>(fun () ->
                    build environment [ DataProtectionStore.EncryptionKeySetting, bad ] |> ignore)

            Assert.Contains(DataProtectionStore.EncryptionKeySetting, ex.Message)
            Assert.DoesNotContain(bad, ex.Message)

[<Fact>]
let ``outside Production the app starts without a key and stores the keys as plain xml`` () =
    task {
        let! db = newDatabase ()

        use! app =
            createAppWith db [ DataProtectionStore.EncryptionKeySetting, "" ] (fun builder ->
                builder.Environment.EnvironmentName <- "Development")

        use! browser = signedUp app "sub-1" "animenerd"
        let! page = browser.Get "/account"
        Assert.Equal("/account", page.Path)

        let! rows = keyRows db
        Assert.NotEmpty rows

        for row in rows do
            Assert.Contains("masterKey", row)
            Assert.DoesNotContain("encryptedSecret", row)
    }

[<Fact>]
let ``the retired KeysPath setting is ignored with a warning and the keys stay in the table`` () =
    task {
        let! db = newDatabase ()
        let path = Path.Combine(Path.GetTempPath(), "frunchy-retired-keys-" + Guid.NewGuid().ToString("N"))
        let logs = new CapturedLogs()

        use! app =
            createAppWith db [ DataProtectionStore.KeysPathSetting, path ] (fun builder -> builder.Logging.AddProvider logs |> ignore)

        use! browser = signedUp app "sub-1" "animenerd"
        let! page = browser.Get "/account"
        Assert.Equal("/account", page.Path)

        let! rows = keyRows db
        Assert.NotEmpty rows
        Assert.False(Directory.Exists path, "nothing may be written to the retired path")
        Assert.Contains(logs.Lines, fun line -> line.Contains DataProtectionStore.KeysPathSetting && line.Contains "retired")
    }

// ---------------------------------------------------------------------------
// Tampering
// ---------------------------------------------------------------------------

/// The same row with one byte of the encrypted value changed
let private tamper (xml: string) =
    let pattern = Regex "<value>([A-Za-z0-9+/]+=*)</value>"
    let found = pattern.Match xml
    Assert.True(found.Success, "no encrypted value found in the stored key")
    let value = found.Groups[1].Value
    let index = value.Length / 2
    let flipped = if value[index] = 'A' then 'B' else 'A'
    xml.Substring(0, found.Groups[1].Index + index) + string flipped + xml.Substring(found.Groups[1].Index + index + 1)

[<Fact>]
let ``a changed byte in an encrypted value fails GCM's check and is never decrypted`` () =
    let key = Convert.FromBase64String TestDataProtectionKey
    let services = ServiceCollection()
    services.AddSingleton(DataProtectionStore.DataProtectionEncryptionKey key) |> ignore
    use provider = services.BuildServiceProvider()

    let secret = XElement(XName.Get "masterKey", XElement(XName.Get "value", "c2VjcmV0LW1hdGVyaWFs"))
    let encrypted = (DataProtectionStore.AesGcmXmlEncryptor key :> IXmlEncryptor).Encrypt secret
    let decryptor = DataProtectionStore.AesGcmXmlDecryptor provider :> IXmlDecryptor

    // Untouched: the secret comes back
    Assert.Equal(secret.ToString(), decryptor.Decrypt(encrypted.EncryptedElement).ToString())

    // One byte changed, in each of the nonce, the tag and the ciphertext
    let value = encrypted.EncryptedElement.Element(XName.Get "value")
    let original = Convert.FromBase64String value.Value

    for position in [ 0; 12; 28; original.Length - 1 ] do
        let changed = Array.copy original
        changed[position] <- changed[position] ^^^ 1uy
        let element = XElement encrypted.EncryptedElement
        element.Element(XName.Get "value").Value <- Convert.ToBase64String changed
        Assert.ThrowsAny<CryptographicException>(fun () -> decryptor.Decrypt element |> ignore) |> ignore

    // A key that isn't the one it was encrypted with fails too
    let otherServices = ServiceCollection()
    otherServices.AddSingleton(DataProtectionStore.DataProtectionEncryptionKey(RandomNumberGenerator.GetBytes 32)) |> ignore
    use otherProvider = otherServices.BuildServiceProvider()
    let other = DataProtectionStore.AesGcmXmlDecryptor otherProvider :> IXmlDecryptor
    Assert.ThrowsAny<CryptographicException>(fun () -> other.Decrypt encrypted.EncryptedElement |> ignore) |> ignore

[<Fact>]
let ``a tampered stored key is skipped and the old cookies stop working`` () =
    task {
        let! db = newDatabase ()
        let! cookies = signedInOnAStoppedApp db

        let! rows = keyRows db
        Assert.Equal(1, rows.Length)
        let tampered = tamper rows[0]
        Assert.NotEqual<string>(rows[0], tampered)

        do!
            task {
                use connection = Database.getConnection db
                do! connection.OpenAsync()
                use cmd = new Npgsql.NpgsqlCommand("UPDATE data_protection_keys SET xml = @xml", connection)
                cmd.Parameters.AddWithValue("xml", tampered) |> ignore
                let! _ = cmd.ExecuteNonQueryAsync()
                ()
            }

        // The copy still starts: the unreadable key is skipped and a new one is made
        use! after = createApp db
        use browser = after.NewBrowser()
        browser.Cookies.Add cookies
        let! page = browser.Get "/account"
        Assert.StartsWith("/signin", page.Path)

        let! count = Db.count db "data_protection_keys"
        Assert.Equal(2L, count)
    }
