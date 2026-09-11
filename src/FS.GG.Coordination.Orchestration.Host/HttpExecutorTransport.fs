namespace FS.GG.Coordination.Orchestration.Host

open System
open System.Buffers.Binary
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks

[<Sealed>]
type HttpExecutorTransport(client:HttpClient,endpoint:Uri,bearerToken:string,maximumResponseBytes:int) =
    let frame values =
        use stream=new MemoryStream()
        for value:byte array in values do
            let header=Array.zeroCreate<byte> 4
            BinaryPrimitives.WriteInt32BigEndian(header,value.Length)
            stream.Write header;stream.Write value
        stream.ToArray()
    let unframe (bytes:byte array) =
        let values=ResizeArray<byte array>()
        let mutable offset=0
        let mutable valid=true
        while valid && offset<bytes.Length do
            if bytes.Length-offset<4 then valid<-false else
            let size=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset,4))
            offset<-offset+4
            if size<1 || size>2*1024*1024 || size>bytes.Length-offset then valid<-false
            else values.Add bytes[offset..offset+size-1];offset<-offset+size
        if valid && offset=bytes.Length then Ok(List.ofSeq values) else Error "executor-http-response-framing-refused"
    interface IAuthenticatedExecutorTransport with
        member _.Exchange(frames,token)=task {
            if isNull endpoint || endpoint.Scheme<>Uri.UriSchemeHttp || not endpoint.IsLoopback || String.IsNullOrWhiteSpace bearerToken || maximumResponseBytes<1 || maximumResponseBytes>150*1024*1024 then return Error "executor-http-binding-refused"
            else
                use request=new HttpRequestMessage(HttpMethod.Post,endpoint)
                request.Headers.Authorization<-AuthenticationHeaderValue("Bearer",bearerToken)
                request.Content<-new ByteArrayContent(frame frames)
                request.Content.Headers.ContentType<-MediaTypeHeaderValue("application/vnd.fsgg.executor-frames")
                try
                    use! response=client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token)
                    if not response.IsSuccessStatusCode then return Error($"executor-http-status-refused:{int response.StatusCode}")
                    elif response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value>int64 maximumResponseBytes then return Error "executor-http-response-size-refused"
                    else
                        use! input=response.Content.ReadAsStreamAsync token
                        use output=new MemoryStream()
                        let buffer=Array.zeroCreate<byte> 8192
                        let mutable total=0
                        let mutable reading=true
                        let mutable overflow=false
                        while reading do
                            let! read=input.ReadAsync(Memory buffer,token)
                            if read=0 then reading<-false
                            elif total+read>maximumResponseBytes then overflow<-true;reading<-false
                            else output.Write(buffer,0,read);total<-total+read
                        if overflow then return Error "executor-http-response-size-refused"
                        else return unframe(output.ToArray())|>Result.map(fun values->{Frames=values})
                with :? OperationCanceledException -> return Error "executor-http-timeout-or-cancelled" | error -> return Error($"executor-http-ambiguous:{error.GetType().Name}") }
