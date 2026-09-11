open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Runtime.InteropServices
open System.Text
open System.Threading
open Microsoft.Win32.SafeHandles
open FS.GG.Coordination.Orchestration.Runner.Protocol

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private Timespec = { Seconds:int64; Nanoseconds:int64 }
[<Struct; StructLayout(LayoutKind.Sequential)>]
type private LinuxStat =
    { Device:uint64;Inode:uint64;Links:uint64;Mode:uint32;UserId:uint32;GroupId:uint32;Padding:int32
      SpecialDevice:uint64;Size:int64;BlockSize:int64;Blocks:int64;Access:Timespec;Modification:Timespec;Change:Timespec
      Reserved0:int64;Reserved1:int64;Reserved2:int64 }
[<DllImport("libc",EntryPoint="lstat",SetLastError=true)>]
extern int private lstat(string path,LinuxStat& value)
[<DllImport("libc",EntryPoint="fstat",SetLastError=true)>]
extern int private fstat(nativeint descriptor,LinuxStat& value)
[<DllImport("libc",EntryPoint="geteuid")>]
extern uint32 private geteuid()

let private usage () =
    eprintfn "usage: fsgg-coord-orchestration-runner post --endpoint <https-url> --token-file <owner-only-file> --path </v1/runner/...> --request-file <closed-json>"
    2

let private pairs (arguments:string array) =
    if arguments.Length%2<>0 then Error "options-must-be-pairs"
    else
        arguments |> Array.chunkBySize 2
        |> Array.fold(fun state pair -> state |> Result.bind(fun values -> if Map.containsKey pair[0] values then Error "duplicate-option" else Ok(Map.add pair[0] pair[1] values))) (Ok Map.empty)

let private privateToken (path:string) =
    try
        if not(Path.IsPathFullyQualified path) then Error "token-path-must-be-absolute"
        else
            let mutable before=Unchecked.defaultof<LinuxStat>
            if lstat(path,&before)<>0 || before.Mode &&& 0xF000u<>0x8000u || before.UserId<>geteuid() || before.Size<=0L || before.Size>4096L then Error "token-file-refused"
            else
                let parent=DirectoryInfo(Path.GetDirectoryName path)
                let parentWritable=File.GetUnixFileMode(parent.FullName) &&& (UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite)
                let exposed=enum<UnixFileMode>(int before.Mode) &&& (UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite ||| UnixFileMode.OtherExecute)
                if not(isNull parent.LinkTarget) || parentWritable<>enum 0 || exposed<>enum 0 then Error "token-file-must-be-owner-only"
                else
                    use stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.SequentialScan)
                    let mutable opened=Unchecked.defaultof<LinuxStat>
                    if fstat(stream.SafeFileHandle.DangerousGetHandle(),&opened)<>0 || opened.Device<>before.Device || opened.Inode<>before.Inode || opened.UserId<>before.UserId || opened.Mode<>before.Mode || opened.Size<>before.Size then Error "token-file-changed-during-open"
                    else
                        use reader=new StreamReader(stream)
                        let buffer=Array.zeroCreate<char> 4097
                        let count=reader.ReadBlock(buffer,0,buffer.Length)
                        let value=String(buffer,0,count).Trim()
                        if count>4096 || value.Length<32 then Error "token-size-refused" else Ok value
    with _ -> Error "token-file-refused"

let private validRequest path bytes =
    match path with
    | "/v1/runner/assignment" -> RunnerWire.parsePoll bytes |> Result.map ignore
    | "/v1/runner/ack" -> RunnerWire.parseAck bytes |> Result.map ignore
    | "/v1/runner/candidate" -> RunnerWire.parseCandidate bytes |> Result.map ignore
    | _ -> Error "runner-path-refused"

[<EntryPoint>]
let main arguments =
    if not(OperatingSystem.IsLinux()) || RuntimeInformation.ProcessArchitecture<>Architecture.X64 || Array.tryHead arguments<>Some "post" then usage()
    else
        match pairs arguments[1..] with
        | Error reason -> eprintfn "%s" reason; 2
        | Ok values ->
            let allowed=set["--endpoint";"--token-file";"--path";"--request-file"]
            if values.Count<>allowed.Count || values |> Map.exists(fun key _ -> not(Set.contains key allowed)) then usage()
            else
                match Uri.TryCreate(values["--endpoint"],UriKind.Absolute),privateToken values["--token-file"] with
                | (true,endpoint),Ok token when endpoint.Scheme=Uri.UriSchemeHttps && endpoint.UserInfo="" && endpoint.Query="" && endpoint.Fragment="" && endpoint.AbsolutePath="/" ->
                    try
                        let bytes=File.ReadAllBytes(values["--request-file"])
                        match validRequest values["--path"] bytes with
                        | Error reason -> eprintfn "runner-request-refused:%s" reason; 2
                        | Ok() ->
                            use handler=new HttpClientHandler(AllowAutoRedirect=false)
                            use client=new HttpClient(handler,Timeout=TimeSpan.FromSeconds 30.)
                            client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer",token)
                            use content=new ByteArrayContent(bytes)
                            content.Headers.ContentType <- Headers.MediaTypeHeaderValue("application/json")
                            use response=client.PostAsync(Uri(endpoint,values["--path"]),content,CancellationToken.None).GetAwaiter().GetResult()
                            let responseBytes=response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                            if responseBytes.Length>140*1024*1024 then eprintfn "runner-response-size-refused"; 3
                            else
                                Console.OpenStandardOutput().Write(responseBytes)
                                if response.IsSuccessStatusCode then 0 else 3
                    with error -> eprintfn "runner-request-refused:%s" error.Message; 3
                | _ -> eprintfn "runner-endpoint-or-token-refused"; 2
