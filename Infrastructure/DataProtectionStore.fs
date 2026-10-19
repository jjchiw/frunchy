/// Feature 016: the ASP.NET Core data-protection keys (session cookies, sign-in round trips, form tokens) live in
/// Postgres, so a restart, a release or a second copy reads the same keys and nobody is signed out
/// (specs/016-deploy-readiness/research.md R1 to R3). When `Frunchy:DataProtection:EncryptionKey` is set, the key
/// material is stored encrypted with AES-256-GCM, so a leaked backup doesn't hold what signs every session.
module FrunchyBackend.Infrastructure.DataProtectionStore

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Xml.Linq
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.DataProtection.KeyManagement
open Microsoft.AspNetCore.DataProtection.Repositories
open Microsoft.AspNetCore.DataProtection.XmlEncryption
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Npgsql
open FrunchyBackend.Infrastructure.Database

[<Literal>]
let EncryptionKeySetting = "Frunchy:DataProtection:EncryptionKey"

/// Retired: the keys are always in Postgres now; setting it only logs a warning
[<Literal>]
let KeysPathSetting = "Frunchy:DataProtection:KeysPath"

/// One name for every copy, so the key ring doesn't depend on the content path
[<Literal>]
let ApplicationName = "frunchy"

/// Bound into every encrypted value, so one made for another purpose can't be passed off as a key
let private associatedData = Encoding.UTF8.GetBytes "frunchy-data-protection-v1"

let private nonceSize = 12
let private tagSize = 16

/// The 32-byte key the stored keys are encrypted with; a singleton so the decryptor (which the framework
/// activates itself) can find it
type DataProtectionEncryptionKey(bytes: byte[]) =
    member _.Bytes = bytes

/// The framework's key store, read and written through Npgsql's synchronous API because `IXmlRepository` is
/// synchronous; it is called at startup, on a refresh (about daily) and when a key is created (every 90 days)
type PostgresXmlRepository(db: DbConfig) =
    interface IXmlRepository with
        member _.GetAllElements() =
            use connection = getConnection db
            connection.Open()
            use cmd = new NpgsqlCommand("SELECT xml FROM data_protection_keys ORDER BY id", connection)
            use reader = cmd.ExecuteReader()
            let elements = List<XElement>()

            while reader.Read() do
                elements.Add(XElement.Parse(reader.GetString 0))

            elements :> IReadOnlyCollection<XElement>

        member _.StoreElement(element: XElement, friendlyName: string) =
            // Two copies may store the same name; the first wins and the second is ignored
            let name =
                if String.IsNullOrEmpty friendlyName then
                    "element-" + Guid.NewGuid().ToString("N")
                else
                    friendlyName

            use connection = getConnection db
            connection.Open()

            use cmd =
                new NpgsqlCommand(
                    "INSERT INTO data_protection_keys (friendly_name, xml) VALUES (@name, @xml) ON CONFLICT (friendly_name) DO NOTHING",
                    connection
                )

            cmd.Parameters.AddWithValue("name", name) |> ignore
            cmd.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting)) |> ignore
            cmd.ExecuteNonQuery() |> ignore

/// Encrypts a key's secret part: `<encryptedKey alg="AES-256-GCM" v="1"><value>base64(nonce, tag, ciphertext)</value></encryptedKey>`
type AesGcmXmlEncryptor(key: byte[]) =
    interface IXmlEncryptor with
        member _.Encrypt(plaintextElement: XElement) =
            let plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting))
            let nonce = RandomNumberGenerator.GetBytes nonceSize
            let tag = Array.zeroCreate<byte> tagSize
            let ciphertext = Array.zeroCreate<byte> plaintext.Length
            use aes = new AesGcm(key, tagSize)
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData)

            let value = Convert.ToBase64String(Array.concat [ nonce; tag; ciphertext ])

            let element =
                XElement(
                    XName.Get "encryptedKey",
                    XAttribute(XName.Get "alg", "AES-256-GCM"),
                    XAttribute(XName.Get "v", "1"),
                    XElement(XName.Get "value", value)
                )

            EncryptedXmlInfo(element, typeof<AesGcmXmlDecryptor>)

/// The framework activates this through the `IServiceProvider` constructor. A value changed by even one byte fails
/// GCM's tag check and throws, so a tampered key is never decrypted wrongly.
and AesGcmXmlDecryptor(services: IServiceProvider) =
    interface IXmlDecryptor with
        member _.Decrypt(encryptedElement: XElement) =
            let key = services.GetRequiredService<DataProtectionEncryptionKey>().Bytes
            let value = Convert.FromBase64String(encryptedElement.Element(XName.Get "value").Value)

            if value.Length < nonceSize + tagSize then
                raise (CryptographicException "The stored key is too short to be an encrypted key.")

            let nonce = value[.. nonceSize - 1]
            let tag = value[nonceSize .. nonceSize + tagSize - 1]
            let ciphertext = value[nonceSize + tagSize ..]
            let plaintext = Array.zeroCreate<byte> ciphertext.Length
            use aes = new AesGcm(key, tagSize)
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData)
            XElement.Parse(Encoding.UTF8.GetString plaintext)

/// Base64 of exactly 32 bytes. The error names the setting and never the value.
let parseKey (value: string) : Result<byte[], string> =
    let invalid =
        $"{EncryptionKeySetting} must be the base64 of exactly 32 bytes (for example the output of `openssl rand -base64 32`)."

    try
        let bytes = Convert.FromBase64String value
        if bytes.Length = 32 then Ok bytes else Error invalid
    with :? FormatException ->
        Error invalid

/// Called from `Program.buildApp` after `configureServices`:
/// - a key that isn't base64 of 32 bytes stops startup, in every environment;
/// - no key stops startup in Production (the stored keys would be plain XML);
/// - elsewhere without a key the framework stores plain XML and logs its own warning.
let register (environment: IHostEnvironment) (config: IConfiguration) (services: IServiceCollection) =
    let encryptionKey =
        match config[EncryptionKeySetting] with
        | null
        | "" ->
            if environment.IsProduction() then
                raise (
                    InvalidOperationException
                        $"{EncryptionKeySetting} is required in Production: it encrypts the data-protection keys stored in the database. Set it to the output of `openssl rand -base64 32` and back it up apart from the database."
                )

            None
        | value ->
            match parseKey value with
            | Ok bytes -> Some bytes
            | Error message -> raise (InvalidOperationException message)

    services.AddDataProtection().SetApplicationName ApplicationName |> ignore

    services
        .AddOptions<KeyManagementOptions>()
        .Configure<DbConfig>(fun options db -> options.XmlRepository <- PostgresXmlRepository db)
    |> ignore

    match encryptionKey with
    | Some bytes ->
        services.AddSingleton<DataProtectionEncryptionKey>(DataProtectionEncryptionKey bytes) |> ignore

        services
            .AddOptions<KeyManagementOptions>()
            .Configure(fun (options: KeyManagementOptions) -> options.XmlEncryptor <- AesGcmXmlEncryptor bytes)
        |> ignore
    | None -> ()
