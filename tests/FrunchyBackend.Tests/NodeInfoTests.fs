/// Feature 028 (T002, FR-016): the NodeInfo check that says whether a typed host is a Frunchy instance, and the
/// address rule behind its connection guard. NodeInfo comes from `FakeNodeInfo`, never the network.
module FrunchyBackend.Tests.NodeInfoTests

open System.Net
open System.Net.Http
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.NodeInfo
open FrunchyBackend.Tests.Infrastructure.FakeNodeInfo

let private clientOver (fake: FakeNodeInfoHandler) = new HttpClient(fake, false)

let private checkWith (setup: FakeNodeInfoHandler -> unit) (host: string) =
    task {
        let fake = new FakeNodeInfoHandler()
        setup fake
        use client = clientOver fake
        let! result = NodeInfo.check client host
        return result, fake
    }

[<Fact>]
let ``a frunchy instance is recognised, whatever the case of its name`` () =
    task {
        let! result, fake = checkWith (fun f -> f.Set("other.test", Frunchy)) "other.test"
        Assert.Equal(IsFrunchy, result)

        // discovery first, then the schema document it links to, both over https on the same host
        Assert.Equal<string list>([ "https://other.test/.well-known/nodeinfo"; "https://other.test/nodeinfo/2.0" ], fake.Requests)

        let! upper = checkWith (fun f -> f.Set("other.test", Software "FRUNCHY")) "OTHER.test"
        Assert.Equal(IsFrunchy, fst upper)
    }

[<Fact>]
let ``another server is not frunchy`` () =
    task {
        for name in [ "mastodon"; "gotosocial"; "frunchy-fork"; "" ] do
            let! result, _ = checkWith (fun f -> f.Set("masto.test", Software name)) "masto.test"
            Assert.Equal(NotFrunchy, result)
    }

[<Fact>]
let ``a discovery link to another host is not followed and says not frunchy`` () =
    task {
        let! result, fake = checkWith (fun f -> f.Set("a.test", OffHost "evil.test")) "a.test"
        Assert.Equal(NotFrunchy, result)
        Assert.Equal<string list>([ "https://a.test/.well-known/nodeinfo" ], fake.Requests)
    }

[<Fact>]
let ``no schema link, a body that is not json and a bad host name say not frunchy`` () =
    task {
        let! noLink, _ = checkWith (fun f -> f.Set("a.test", NoSchemaLink)) "a.test"
        Assert.Equal(NotFrunchy, noLink)
        let! garbage, _ = checkWith (fun f -> f.Set("a.test", Garbage)) "a.test"
        Assert.Equal(NotFrunchy, garbage)

        // a host with a port, a path or a scheme never makes a call
        for host in [ "a.test:8443"; "a.test/x"; "https://a.test"; "a b"; ""; null ] do
            let! result, fake = checkWith (fun f -> f.Set("a.test", Frunchy)) host
            Assert.Equal(NotFrunchy, result)
            Assert.Empty fake.Requests
    }

[<Fact>]
let ``no answer, an error status, a redirect and an oversized answer are unreachable`` () =
    task {
        for behavior in [ Down; Status 500; Status 404; Redirect "elsewhere.test"; Oversize ] do
            let! result, _ = checkWith (fun f -> f.Set("a.test", behavior)) "a.test"
            Assert.Equal(Unreachable, result)

        // an unknown host is down
        let! unknown, _ = checkWith ignore "nobody.test"
        Assert.Equal(Unreachable, unknown)
    }

// ---------------------------------------------------------------------------
// The address rule
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData("0.0.0.0")>]
[<InlineData("0.1.2.3")>]
[<InlineData("10.0.0.1")>]
[<InlineData("10.255.255.255")>]
[<InlineData("100.64.0.1")>]
[<InlineData("100.127.255.255")>]
[<InlineData("127.0.0.1")>]
[<InlineData("127.9.9.9")>]
[<InlineData("169.254.169.254")>]
[<InlineData("172.16.0.1")>]
[<InlineData("172.31.255.255")>]
[<InlineData("192.168.1.1")>]
[<InlineData("224.0.0.1")>]
[<InlineData("239.255.255.250")>]
[<InlineData("240.0.0.1")>]
[<InlineData("255.255.255.255")>]
[<InlineData("::")>]
[<InlineData("::1")>]
[<InlineData("fc00::1")>]
[<InlineData("fd12:3456::1")>]
[<InlineData("fe80::1")>]
[<InlineData("febf::1")>]
[<InlineData("ff02::1")>]
[<InlineData("::ffff:127.0.0.1")>]
[<InlineData("::ffff:10.1.2.3")>]
[<InlineData("::ffff:169.254.169.254")>]
[<InlineData("::ffff:192.168.0.9")>]
[<InlineData("2002:0a00:0001::1")>]
[<InlineData("2002:7f00:0001::")>]
[<InlineData("64:ff9b::a9fe:a9fe")>]
[<InlineData("198.18.0.1")>]
[<InlineData("198.19.255.255")>]
[<InlineData("192.0.0.8")>]
let ``a private, loopback, link-local, multicast or unspecified address is refused`` (address: string) =
    Assert.False(isPublicAddress (IPAddress.Parse address), address)

[<Theory>]
[<InlineData("1.1.1.1")>]
[<InlineData("8.8.8.8")>]
[<InlineData("93.184.216.34")>]
[<InlineData("100.63.255.255")>]
[<InlineData("100.128.0.1")>]
[<InlineData("172.15.255.255")>]
[<InlineData("172.32.0.1")>]
[<InlineData("169.253.1.1")>]
[<InlineData("192.169.0.1")>]
[<InlineData("223.255.255.255")>]
[<InlineData("2606:4700:4700::1111")>]
[<InlineData("2001:4860:4860::8888")>]
[<InlineData("::ffff:8.8.8.8")>]
[<InlineData("2002:0808:0808::1")>]
[<InlineData("64:ff9b::808:808")>]
[<InlineData("198.20.0.1")>]
let ``a public address is allowed`` (address: string) =
    Assert.True(isPublicAddress (IPAddress.Parse address), address)
