namespace FS.GG.FourD.Typed

open System
open System.IO
open System.Text.Json

/// Closed process boundary. The existing qualified IO adapter owns journal continuity and
/// raw HTTP/file observations; no portable authentication or new control plane is implied.
module NativeProtocol =
    open NativeJoin
    let private options = JsonSerializerOptions(PropertyNamingPolicy=JsonNamingPolicy.CamelCase)
    let private write output value = File.WriteAllText(output,JsonSerializer.Serialize(value,options)+"\n")
    let private reason (error:exn) =
        match error with
        | :? ArgumentException as argument ->
            let code=argument.Message.Split([|' ';'\r';'\n'|],StringSplitOptions.RemoveEmptyEntries)[0]
            if token code then code else "native-input-refused"
        | _ -> "native-input-refused"
    let execute command input output =
        try
            let request=json input
            match command with
            | "native-runtime-info" ->
                closed ["schema"] request
                require (str request "schema"="fsgg.fourd.native-runtime-info-request/1") "native-schema-refused"
                write output {|schema="fsgg.fourd.native-runtime-info/1";runtimeVersion=Environment.Version.ToString()|}
                0
            | "native-join" ->
                closed ["schema";"producer"] request
                require (str request "schema"="fsgg.fourd.native-producer-request/1") "native-schema-refused"
                let evidence=request.GetProperty "producer"
                let p=acquireProducer evidence
                write output {|schema="fsgg.fourd.native-producer-join/1";identity=producerIdentity p
                               sourceSha=str evidence "expectedSourceSha";sourceTree=str evidence "expectedSourceTree"
                               bindingSha256=str evidence "expectedBindingSha256";provenanceSha256=str evidence "expectedProvenanceSha256"|}
                0
            | "native-root" ->
                closed ["schema";"producer";"root"] request
                require (str request "schema"="fsgg.fourd.native-root-request/1") "native-schema-refused"
                let p=acquireProducer (request.GetProperty "producer")
                let r=acquireRoot p (request.GetProperty "root")
                write output {|schema="fsgg.fourd.native-root-join/1";identity=rootIdentity r;producerIdentity=producerIdentity p;mode=rootMode r;packet=packet r|}
                0
            | "native-transition" ->
                closed ["schema";"mode";"startedUnixSeconds";"history";"observation"] request
                require (str request "schema"="fsgg.fourd.native-transition-request/1") "native-schema-refused"
                let events=request.GetProperty "history"
                require (events.ValueKind=JsonValueKind.Array && events.GetArrayLength()<=256) "native-history-bound-refused"
                let mutable state=NativePolicy.initial (str request "mode") (number request "startedUnixSeconds")
                let history=ResizeArray<JsonElement>()
                for e in events.EnumerateArray() do
                    let normalized,next=NativePolicy.apply true state e
                    history.Add normalized
                    state<-next
                let fresh=request.GetProperty "observation"
                let normalized,next,eventAccepted =
                    try
                        let normalized,next=NativePolicy.apply false state fresh
                        normalized,next,true
                    with error ->
                        // A failed fresh constructor is itself sticky evidence. Persist it in
                        // the qualified journal so a later valid retry cannot erase refusal.
                        let code=reason error
                        let elapsed =
                            try
                                let n=number fresh "elapsed"
                                let current=(NativePolicy.projection state).elapsed
                                if n>=int64 current && n<=int64(NativePolicy.budget state) then int n else current
                            with _ -> (NativePolicy.projection state).elapsed
                        let remote =
                            try let k=str fresh "kind" in k="remote" || k="remote-credential-retirement"
                            with _ -> false
                        let refused=JsonSerializer.SerializeToElement({|kind=(if remote then "root-refuse" else "refuse");elapsed=elapsed;reason=code|})
                        let normalized,next=NativePolicy.apply false state refused
                        normalized,next,false
                history.Add normalized
                let projection=NativePolicy.projection next
                write output {|schema="fsgg.fourd.native-transition/1";eventAccepted=eventAccepted;history=history.ToArray();state=projection;imageBinding=NativePolicy.admittedImageBinding next;modelState=NativePolicy.modelProjection next;actions=NativePolicy.actions next;refusal=(if isNull projection.refusal then projection.rootObservationRefusal else projection.refusal)|}
                if isNull projection.refusal && isNull projection.rootObservationRefusal then 0 else 2
            | _ -> fail "native-command-refused"
        with error ->
            // No caller-supplied success/cleanup field survives malformed or stale evidence.
            let refusal=reason error
            write output {|schema="fsgg.fourd.native-refusal/1";refusal=refusal;actions=[|"begin-cleanup"|]|}
            2
