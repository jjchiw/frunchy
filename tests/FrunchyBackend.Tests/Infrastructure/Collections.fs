namespace FrunchyBackend.Tests.Infrastructure

open Xunit

module Collections =
    /// Tests that measure time run alone: other test classes running beside them share the machine and the
    /// Postgres container, which skews the timings (the 2M-row chart test took 6 s only when the series page
    /// tests ran next to it).
    [<Literal>]
    let Timed = "Timed tests, run alone"

[<CollectionDefinition(Collections.Timed, DisableParallelization = true)>]
type TimedCollection() = class end
