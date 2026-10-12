namespace FS.GG.Execution.Processes.Tests
open System.Reflection
open Xunit
open FS.GG.Execution.Contracts
open FS.GG.Execution.Processes

module PublicSurfaceTests =
    [<Fact>]
    let ``signature exports explicit policy without process or pid authority`` () =
        let assembly = ProcessSessions.createFactory().GetType().Assembly
        let exported = assembly.GetExportedTypes()
        let allowed =
            set [ "FS.GG.Execution.Processes.ProcessSessions"
                  "FS.GG.Execution.Processes.RetirementPolicy"
                  "FS.GG.Execution.Processes.RetirementPolicy+Tags" ]
        Assert.All(exported, fun api -> Assert.Contains(api.FullName,allowed))
        Assert.Contains(exported, fun api -> api = typeof<RetirementPolicy>)
        let api = assembly.GetType("FS.GG.Execution.Processes.ProcessSessions",true)
        let methods = api.GetMethods(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly)
        Assert.Equal<string>([| "createFactory"; "createFactoryWithRetirement" |],methods |> Array.map _.Name |> Array.sort)
        for methodInfo in methods do Assert.Equal(typeof<IProcessSessionFactory>,methodInfo.ReturnType)
        let selection = methods |> Array.find (fun methodInfo -> methodInfo.Name = "createFactoryWithRetirement")
        Assert.Equal(typeof<RetirementPolicy>,Assert.Single(selection.GetParameters()).ParameterType)

        let friend = api.GetMethod("createFactoryForTesting",BindingFlags.NonPublic ||| BindingFlags.Static)
        Assert.NotNull(friend)
        Assert.True(friend.IsAssembly)
        let friends = assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>() |> Seq.toArray
        Assert.Equal("FS.GG.Execution.Processes.Tests",Assert.Single(friends).AssemblyName)
