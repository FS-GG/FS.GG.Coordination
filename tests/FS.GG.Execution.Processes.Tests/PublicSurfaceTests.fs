namespace FS.GG.Execution.Processes.Tests
open System.Reflection
open Xunit
open FS.GG.Execution.Processes

module PublicSurfaceTests =
    [<Fact>]
    let ``signature exports factory creation without process or pid authority`` () =
        let assembly = ProcessSessions.createFactory().GetType().Assembly
        let exported = assembly.GetExportedTypes()
        let api = Assert.Single(exported)
        Assert.Equal("FS.GG.Execution.Processes.ProcessSessions",api.FullName)
        let methods = api.GetMethods(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly)
        Assert.Equal("createFactory",Assert.Single(methods).Name)
