open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Security
open System.Runtime.InteropServices
open System.Security.Cryptography.X509Certificates
open System.Threading
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
    eprintfn "usage: fsgg-coord-orchestration-runner post --endpoint https://orchestration.main.internal:18080/ --client-cert-file <owner-only-pem> --client-key-file <owner-only-pem> --ca-file <owner-only-pem> --path </v1/runner/...> --request-file <closed-json>"
    2

let private pairs (arguments:string array) =
    if arguments.Length%2<>0 then Error "options-must-be-pairs"
    else
        arguments |> Array.chunkBySize 2
        |> Array.fold(fun state pair -> state |> Result.bind(fun values -> if Map.containsKey pair[0] values then Error "duplicate-option" else Ok(Map.add pair[0] pair[1] values))) (Ok Map.empty)

let private privateBytes maximumBytes (path:string) =
    try
        if not(Path.IsPathFullyQualified path) then Error "credential-path-must-be-absolute"
        else
            let mutable before=Unchecked.defaultof<LinuxStat>
            if lstat(path,&before)<>0 || before.Mode &&& 0xF000u<>0x8000u || before.UserId<>geteuid() || before.Size<=0L || before.Size>int64 maximumBytes then Error "credential-file-refused"
            else
                let parent=DirectoryInfo(Path.GetDirectoryName path)
                let parentWritable=File.GetUnixFileMode(parent.FullName) &&& (UnixFileMode.GroupWrite ||| UnixFileMode.OtherWrite)
                let exposed=enum<UnixFileMode>(int before.Mode) &&& (UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite ||| UnixFileMode.OtherExecute)
                if not(isNull parent.LinkTarget) || parentWritable<>enum 0 || exposed<>enum 0 then Error "credential-file-must-be-owner-only"
                else
                    use stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.SequentialScan)
                    let mutable opened=Unchecked.defaultof<LinuxStat>
                    if fstat(stream.SafeFileHandle.DangerousGetHandle(),&opened)<>0 || opened.Device<>before.Device || opened.Inode<>before.Inode || opened.UserId<>before.UserId || opened.Mode<>before.Mode || opened.Size<>before.Size then Error "credential-file-changed-during-open"
                    else
                        let bytes=Array.zeroCreate<byte> (maximumBytes+1)
                        let mutable offset=0
                        let mutable reading=true
                        while reading && offset<bytes.Length do
                            let count=stream.Read(bytes,offset,bytes.Length-offset)
                            if count=0 then reading<-false else offset<-offset+count
                        if offset>maximumBytes then Error "credential-file-size-refused" else Ok bytes[..offset-1]
    with _ -> Error "credential-file-refused"

let private validRequest path bytes =
    match path with
    | "/v1/runner/assignment" -> RunnerWire.parsePoll bytes |> Result.map ignore
    | "/v1/runner/ack" -> RunnerWire.parseAck bytes |> Result.map ignore
    | "/v1/runner/candidate" -> RunnerWire.parseCandidate bytes |> Result.map ignore
    | _ -> Error "runner-path-refused"

let private readBoundedRequest path requestPath =
    let maximum = if path="/v1/runner/candidate" then 140*1024*1024 else 8192
    try
        use stream=new FileStream(requestPath,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.SequentialScan)
        if stream.Length<=0L || stream.Length>int64 maximum then Error "runner-request-size-refused"
        else
            let bytes=Array.zeroCreate<byte> (int stream.Length)
            let mutable offset=0
            while offset<bytes.Length do
                let count=stream.Read(bytes,offset,bytes.Length-offset)
                if count=0 then offset<-bytes.Length+1 else offset<-offset+count
            if offset<>bytes.Length || stream.ReadByte() <> -1 then Error "runner-request-changed-during-read" else Ok bytes
    with _ -> Error "runner-request-file-refused"

let private validResponse path bytes =
    match path with
    | "/v1/runner/assignment" -> RunnerWire.parseAssignment bytes |> Result.map ignore
    | "/v1/runner/ack" -> RunnerWire.parseAckReceipt bytes |> Result.map ignore
    | "/v1/runner/candidate" -> RunnerWire.parseCandidateReceipt bytes |> Result.map ignore
    | _ -> Error "runner-path-refused"

let private readBoundedResponse (response:HttpResponseMessage) =
    let contentLength=response.Content.Headers.ContentLength
    if contentLength.HasValue && contentLength.Value>8192L then Error "runner-response-size-refused"
    else
        use stream=response.Content.ReadAsStream()
        use output=new MemoryStream()
        let buffer=Array.zeroCreate<byte> 4096
        let mutable total=0
        let mutable reading=true
        while reading && total<=8192 do
            let count=stream.Read(buffer,0,buffer.Length)
            if count=0 then reading<-false else output.Write(buffer,0,count);total<-total+count
        if total>8192 then Error "runner-response-size-refused" else Ok(output.ToArray())

[<EntryPoint>]
let main arguments =
    if not(OperatingSystem.IsLinux()) || RuntimeInformation.ProcessArchitecture<>Architecture.X64 || Array.tryHead arguments<>Some "post" then usage()
    else
        match pairs arguments[1..] with
        | Error reason -> eprintfn "%s" reason; 2
        | Ok values ->
            let allowed=set["--endpoint";"--client-cert-file";"--client-key-file";"--ca-file";"--path";"--request-file"]
            if values.Count<>allowed.Count || values |> Map.exists(fun key _ -> not(Set.contains key allowed)) then usage()
            else
                match Uri.TryCreate(values["--endpoint"],UriKind.Absolute) with
                | true,endpoint when endpoint.Scheme=Uri.UriSchemeHttps && endpoint.Host="orchestration.main.internal" && endpoint.Port=18080 && endpoint.UserInfo="" && endpoint.Query="" && endpoint.Fragment="" && endpoint.AbsolutePath="/" ->
                    match readBoundedRequest values["--path"] values["--request-file"] with
                    | Error reason -> eprintfn "%s" reason;2
                    | Ok requestBytes ->
                      match privateBytes 16384 values["--client-cert-file"],privateBytes 16384 values["--client-key-file"],privateBytes 16384 values["--ca-file"] with
                      | Ok certBytes,Ok keyBytes,Ok caBytes ->
                        try
                            match validRequest values["--path"] requestBytes with
                              | Error reason -> eprintfn "runner-request-refused:%s" reason;2
                              | Ok() ->
                                let certPem=System.Text.Encoding.UTF8.GetString certBytes
                                let keyPem=System.Text.Encoding.UTF8.GetString keyBytes
                                use clientCertificate=X509Certificate2.CreateFromPem(certPem.AsSpan(),keyPem.AsSpan())
                                let caPem=System.Text.Encoding.UTF8.GetString caBytes
                                use authority=X509Certificate2.CreateFromPem(caPem.AsSpan())
                                use handler=new HttpClientHandler(AllowAutoRedirect=false)
                                handler.ClientCertificates.Add clientCertificate |> ignore
                                handler.ServerCertificateCustomValidationCallback <- fun _ certificate _ errors ->
                                    if isNull certificate || (errors &&& SslPolicyErrors.RemoteCertificateNameMismatch)<>SslPolicyErrors.None then false
                                    else
                                        use chain=new X509Chain()
                                        chain.ChainPolicy.TrustMode<-X509ChainTrustMode.CustomRootTrust
                                        chain.ChainPolicy.CustomTrustStore.Add authority |> ignore
                                        chain.ChainPolicy.RevocationMode<-X509RevocationMode.NoCheck
                                        chain.ChainPolicy.VerificationFlags<-X509VerificationFlags.NoFlag
                                        chain.Build certificate
                                use client=new HttpClient(handler,Timeout=TimeSpan.FromSeconds 30.)
                                use content=new ByteArrayContent(requestBytes)
                                content.Headers.ContentType <- Headers.MediaTypeHeaderValue("application/json")
                                use response=client.PostAsync(Uri(endpoint,values["--path"]),content,CancellationToken.None).GetAwaiter().GetResult()
                                match readBoundedResponse response with
                                | Error reason -> eprintfn "%s" reason;3
                                | Ok responseBytes when not response.IsSuccessStatusCode -> Console.Error.Write(System.Text.Encoding.UTF8.GetString responseBytes);3
                                | Ok responseBytes ->
                                    let mediaType=if isNull response.Content.Headers.ContentType then "" else response.Content.Headers.ContentType.MediaType
                                    if mediaType<>"application/json" then eprintfn "runner-response-media-type-refused";3
                                    else match validResponse values["--path"] responseBytes with
                                         | Error reason -> eprintfn "%s" reason;3
                                         | Ok() -> Console.OpenStandardOutput().Write(responseBytes);0
                        with error -> eprintfn "runner-request-refused:%s" error.Message;3
                      | _ -> eprintfn "runner-credential-refused";2
                | _ -> eprintfn "runner-endpoint-refused";2
