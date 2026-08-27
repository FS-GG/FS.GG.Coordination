open System
open System.IO
open System.Text

let fail code message =
    eprintfn "ROADMAP_WORK_SKILL_INVALID code=%s message=%s" code message
    Environment.ExitCode <- 3

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--")

if arguments.Length <> 1 then
    fail "arguments" "expected one SKILL.md path after --"
else
    let path = Path.GetFullPath arguments[0]
    if not (File.Exists path) then
        fail "missing" path
    else
        let bytes = File.ReadAllBytes path
        let text = UTF8Encoding(false, true).GetString bytes
        let required =
            [ "name: github-substrate-v2-work"
              "description:"
              "roadmap-work inspect"
              "roadmap-work prerequisites"
              "roadmap-work manifest"
              "roadmap-work gates"
              "Stop at the unit boundary"
              "Project status is not authority" ]
        let forbidden =
            [ "fsgg-coord-engine take"
              "fsgg-coord-engine claim"
              "gh api"
              "gh repo edit"
              "--admin"
              "execute the next unit" ]
        for token in required do
            if not (text.Contains(token, StringComparison.Ordinal)) then fail "required-token" token
        for token in forbidden do
            if text.Contains(token, StringComparison.OrdinalIgnoreCase) then fail "forbidden-authority" token
        if bytes.Length > 12000 then fail "size" "SKILL.md exceeds 12,000 UTF-8 bytes"
        if Environment.ExitCode = 0 then printfn "ROADMAP_WORK_SKILL_OK bytes=%d" bytes.Length
