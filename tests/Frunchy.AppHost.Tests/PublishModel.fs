/// Builds the AppHost's publish-mode model (what `aspire deploy` runs) without building or running it: no Docker,
/// no Caravana, no network. The tests then read what the graph declared.
module PublishModel

open System
open System.Collections.Generic
open System.IO
open System.Linq
open System.Reflection
open Aspire.Hosting
open Aspire.Hosting.ApplicationModel

/// The repository root: the AppHost only uses it to point AddProject at FrunchyBackend.fsproj
let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

/// The master key every test shares: base64 of 32 zero bytes, an obvious fake
let fakeMasterKey = Convert.ToBase64String(Array.zeroCreate<byte> 32)

/// Fake secret values, set in configuration so the tests can check none of them ends up as a literal in the graph
let fakeSecrets =
    [ "megafono-master-key", fakeMasterKey
      "megafono-token", "fake-megafono-token-value"
      "megafono-webhook-secret", "fake-megafono-webhook-value"
      "megafono-db-password", "fake-megafono-db-password-value"
      "db-password", "fake-frunchy-db-password-value"
      "data-protection-key", "fake-data-protection-key-value"
      "umbral-client-secret", "fake-umbral-client-secret-value"
      "mal-client-id", "fake-mal-client-id-value"
      "caravana-token", "fake-caravana-token-value" ]

let private baseSettings =
    [ for name, value in fakeSecrets -> $"Parameters:{name}", value ]
    @ [ "Megafono:ImageTag", "0.2.0" ]

/// A publish-mode builder holding the base settings and then `overrides` (which win)
let publishBuilder (overrides: (string * string) list) =
    let options = DistributedApplicationOptions(Args = [| "--operation"; "publish" |])
    let builder = DistributedApplication.CreateBuilder(options)

    let merged =
        let overridden = overrides |> List.map fst |> Set.ofList
        (baseSettings |> List.filter (fun (key, _) -> not (overridden.Contains key))) @ overrides

    for key, value in merged do
        builder.Configuration[key] <- value

    builder

/// The builder after `publishGraph` ran on it with `overrides`
let buildGraph (overrides: (string * string) list) =
    let builder = publishBuilder overrides
    Frunchy.AppHost.publishGraph builder repoRoot
    builder

/// The services of the Caravana app: every project and container (a Postgres server is a container)
let services (builder: IDistributedApplicationBuilder) =
    builder.Resources
    |> Seq.filter (fun r -> r :? ProjectResource || r :? ContainerResource)
    |> Seq.toList

let service (builder: IDistributedApplicationBuilder) (name: string) =
    builder.Resources |> Seq.find (fun r -> r.Name = name)

let parameterNames (builder: IDistributedApplicationBuilder) =
    builder.Resources
    |> Seq.choose (function
        | :? ParameterResource as p -> Some p.Name
        | _ -> None)
    |> Set.ofSeq

/// One call of the Caravana package's per-resource API (WithCaravanaDomain, ...HealthCheck, ...Backup, ...Memory).
/// The package records each one as an annotation on its "caravana" resource, tagged with the service's name. The
/// annotation types are internal to the package, so they are read by type name and property name through
/// reflection.
type CaravanaCall =
    { Kind: string
      Service: string
      Properties: Map<string, obj> }

let caravanaCalls (builder: IDistributedApplicationBuilder) =
    let caravana = builder.Resources |> Seq.find (fun r -> r.GetType().Name = "CaravanaResource")

    [ for annotation in caravana.Annotations do
          let t = annotation.GetType()

          if t.Namespace = "CosmicChimps.Aspire.Hosting.Caravana" && t.Name.EndsWith "Annotation" then
              let properties =
                  t.GetProperties(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)
                  |> Seq.map (fun p -> p.Name, p.GetValue annotation)
                  |> Map.ofSeq

              { Kind = t.Name.Replace("Caravana", "").Replace("Annotation", "")
                Service = string properties["ServiceName"]
                Properties = properties } ]

/// The calls of one kind ("Domain", "Health", "Backup", "Memory") made for one service
let callsFor (builder: IDistributedApplicationBuilder) (kind: string) (serviceName: string) =
    caravanaCalls builder
    |> List.filter (fun c -> c.Kind = kind && c.Service = serviceName)

/// What one environment entry holds, without resolving any secret: a literal string, a reference to a parameter,
/// an endpoint reference, or an expression (a connection string) made of other references
type EnvValue =
    | Literal of string
    | Param of string
    | Endpoint of resource: string * endpoint: string
    | Expression of string
    | Other of string

let private classify (value: obj) =
    match value with
    | :? string as s -> Literal s
    | :? ParameterResource as p -> Param p.Name
    | :? EndpointReference as e -> Endpoint(e.Resource.Name, e.EndpointName)
    | :? ReferenceExpression as r -> Expression r.ValueExpression
    | null -> Other "null"
    | v -> Other(v.GetType().Name)

/// The environment of a resource as the publish operation sees it: Aspire's own annotations replayed against a
/// publish-mode callback context, and nothing resolved to a value
let environmentOf (builder: IDistributedApplicationBuilder) (resource: IResource) =
    let values = Dictionary<string, obj>()
    let context = EnvironmentCallbackContext(builder.ExecutionContext, resource, values)

    for annotation in resource.Annotations do
        match annotation with
        | :? EnvironmentCallbackAnnotation as callback -> callback.Callback.Invoke(context).Wait()
        | a when a.GetType().Name = "EnvironmentAnnotation" ->
            // Aspire's plain name/value annotation is internal
            let t = a.GetType()
            values[string (t.GetProperty("Name").GetValue a)] <- t.GetProperty("Value").GetValue a
        | _ -> ()

    values |> Seq.map (fun kv -> kv.Key, classify kv.Value) |> Map.ofSeq

let envOf (builder: IDistributedApplicationBuilder) (serviceName: string) =
    environmentOf builder (service builder serviceName)

let endpointsOf (resource: IResource) =
    resource.Annotations.OfType<EndpointAnnotation>() |> Seq.toList

let image (resource: IResource) =
    let annotation = resource.Annotations.OfType<ContainerImageAnnotation>() |> Seq.exactlyOne
    annotation.Image, annotation.Tag
