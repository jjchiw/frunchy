/// Guardrail: values never go into SQL text, only into parameters (CLAUDE.md "Data access").
/// Every `new NpgsqlCommand(...)` in production code must take literal SQL. The only exceptions are
/// listed below, each a fixed fragment chosen by our own code, never a value.
module FrunchyBackend.Tests.SqlGuardrailTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit

/// Interpolated SQL that is allowed: (file, the holes it may contain). Holes must be fixed SQL
/// fragments selected in code, never values.
let private allowedInterpolations =
    Map [ "Infrastructure/MalCatalog.fs", set [ "freshness" ] ]

/// Commands built from a name instead of a literal: (file, name). Each name is checked below to be
/// bound to literal SQL.
let private allowedNames =
    set
        [ "Infrastructure/MalCatalog.fs", "sql" // MalCatalog.exec: every caller passes a literal (checked)
          "Infrastructure/Database.fs", "schema"
          "Infrastructure/Database.fs", "legacyLayoutQuery" ]

let private commandStart = Regex(@"new\s+NpgsqlCommand\(\s*", RegexOptions.Compiled)

/// The holes of an interpolated string starting at `start` (just after `$`)
let private holes (source: string) (start: int) =
    let triple = source.Substring(start).StartsWith "\"\"\""
    let openLen = if triple then 3 else 1
    let closing = if triple then "\"\"\"" else "\""
    let finish = source.IndexOf(closing, start + openLen)
    let body = source.Substring(start + openLen, max 0 (finish - start - openLen))
    let body = body.Replace("{{", "").Replace("}}", "")
    [ for m in Regex.Matches(body, @"\{([^{}]+)\}") -> m.Groups[1].Value.Trim() ]

/// Problems found in one source file (path relative to the repo root, '/' separators)
let violations (file: string) (source: string) : string list =
    [ for m in commandStart.Matches source do
          let at = m.Index + m.Length
          let line = source.Substring(0, m.Index).Split('\n').Length
          let rest = source.Substring at

          if rest.StartsWith "\"" then
              // literal SQL, and nothing glued to it ("…" + value)
              let triple = rest.StartsWith "\"\"\""
              let closing = if triple then "\"\"\"" else "\""
              let finish = rest.IndexOf(closing, closing.Length)
              let after = rest.Substring(finish + closing.Length).TrimStart()

              if not (after.StartsWith "," || after.StartsWith ")") then
                  yield $"{file}:{line}: SQL literal joined with something else; use parameters"
          elif rest.StartsWith "$\"" then
              let allowed = allowedInterpolations.TryFind file |> Option.defaultValue Set.empty
              let found = holes source (at + 1)

              match found |> List.filter (fun h -> not (allowed.Contains h)) with
              | [] when allowedInterpolations.ContainsKey file -> ()
              | [] -> yield $"{file}:{line}: interpolated SQL outside the allowlist"
              | bad -> yield $"""{file}:{line}: interpolated SQL with values {String.Join(", ", bad)}; use parameters"""
          else
              let name = Regex.Match(rest, @"^[A-Za-z_][A-Za-z0-9_']*").Value

              if name = "" || not (allowedNames.Contains(file, name)) then
                  yield $"{file}:{line}: SQL built from '{rest.Substring(0, min 30 rest.Length).Trim()}', not a literal" ]

let private repoRoot () =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then failwith "repo root not found"
        elif File.Exists(Path.Combine(dir.FullName, "FrunchyBackend.slnx")) then dir.FullName
        else up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let private productionSources () =
    let root = repoRoot ()
    let sep = string Path.DirectorySeparatorChar

    Directory.EnumerateFiles(root, "*.fs", SearchOption.AllDirectories)
    |> Seq.filter (fun f ->
        let rel = Path.GetRelativePath(root, f)
        not (rel.StartsWith("tests" + sep)) && not (rel.Contains(sep + "bin" + sep)) && not (rel.Contains(sep + "obj" + sep))
        && not (rel.StartsWith("bin" + sep)) && not (rel.StartsWith("obj" + sep)))
    |> Seq.map (fun f -> Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText f)
    |> List.ofSeq

[<Fact>]
let ``production SQL takes values only as parameters`` () =
    let sources = productionSources ()
    let commands = sources |> List.sumBy (fun (_, s) -> commandStart.Matches(s).Count)
    Assert.True(commands > 30, $"the guardrail found only {commands} commands; is it looking at the right files?")
    let problems = sources |> List.collect (fun (file, source) -> violations file source)
    Assert.True(problems.IsEmpty, String.Join(Environment.NewLine, problems))

[<Fact>]
let ``the commands built from a name get literal SQL`` () =
    let sources = Map(productionSources ())
    let database = sources["Infrastructure/Database.fs"]
    Assert.Matches(@"let private schema =\s*""""""", database)
    Assert.Matches(@"let private legacyLayoutQuery =\s*""""""", database)

    // MalCatalog.exec db <sql> …: every call passes a literal
    let catalog = sources["Infrastructure/MalCatalog.fs"]
    let calls = Regex.Matches(catalog, @"\bexec\s+db\s+(\S)")
    Assert.NotEmpty calls

    for call in calls do
        Assert.Equal("\"", call.Groups[1].Value)

[<Fact>]
let ``the guardrail catches values in SQL text`` () =
    let bad =
        """
        use cmd = new NpgsqlCommand($"SELECT * FROM users WHERE username = '{username}'", conn)
        use two = new NpgsqlCommand(sprintf "DELETE FROM users WHERE id = %s" id, conn)
        use three = new NpgsqlCommand("SELECT 1 WHERE x = '" + value + "'", conn)
        use four = new NpgsqlCommand(query, conn)
        """

    let found = violations "Handlers/Example.fs" bad
    Assert.Equal(4, found.Length)
    Assert.Contains(found, fun p -> p.Contains "username")
    Assert.Contains(found, fun p -> p.Contains "sprintf")
    Assert.Contains(found, fun p -> p.Contains "joined")
    Assert.Contains(found, fun p -> p.Contains "query")

    // An allowlisted fragment is fine; a value next to it is not
    let mixed = "new NpgsqlCommand($\"\"\"UPDATE t SET a = 1{freshness} WHERE id = {id}\"\"\", conn)"
    Assert.Equal<string list>([], violations "Infrastructure/MalCatalog.fs" "new NpgsqlCommand($\"\"\"UPDATE t SET a = 1{freshness}\"\"\", conn)")
    Assert.Single(violations "Infrastructure/MalCatalog.fs" mixed) |> ignore
