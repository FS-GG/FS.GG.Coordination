namespace FS.GG.Coordination.Cli

open System
open System.Globalization
open System.Security.Cryptography
open System.Text

type MigrationHistoricalClaimMarker =
    {
        Worker: string
        LeaseMinutes: int
        Session: string option
        BodySha256: string
    }

[<RequireQualifiedAccess>]
module MigrationHistoricalClaimParser =
    let sourceRepository = "FS-GG/.github"
    let sourceRevision = "95de1c77674b9dd8d7a9ce568d1ee175a7797e5e"
    let sourcePath = "tests/coord-engine-parity/casadversarial_server.py"
    let sourceSha256 = "ce2f01a5bf7983ff98cced126c13a6c8bf7dd7a13198c8b7fda85372b4d9868f"

    let private prefix = "<!-- fsgg:claim "
    let private renewedField = "renewed="
    let private suffix = " -->\nheld"

    let private sha256 (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private parsePositiveCanonicalInt (value: string) =
        let mutable parsed = 0

        if
            Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, &parsed)
            && parsed > 0
            && string parsed = value
        then
            Some parsed
        else
            None

    let private splitField (value: string) =
        let separator = value.IndexOf('=')

        if separator <= 0 || separator = value.Length - 1 then
            None
        else
            Some(value.Substring(0, separator), value.Substring(separator + 1))

    let tryParse (body: string) =
        if isNull body then
            Error "historical-claim-body-null"
        elif not (body.StartsWith(prefix, StringComparison.Ordinal)) then
            Ok None
        elif body.Contains(renewedField, StringComparison.Ordinal) then
            Ok None
        elif not (body.EndsWith(suffix, StringComparison.Ordinal)) then
            Error "historical-claim-wire-shape"
        else
            let fieldsText = body.Substring(prefix.Length, body.Length - prefix.Length - suffix.Length)
            let fields = fieldsText.Split(' ', StringSplitOptions.None) |> Array.toList

            match fields |> List.map splitField with
            | [ Some("worker", worker); Some("lease", lease) ] ->
                match parsePositiveCanonicalInt lease with
                | Some leaseMinutes when not (String.IsNullOrWhiteSpace worker) ->
                    Ok(
                        Some
                            {
                                Worker = worker
                                LeaseMinutes = leaseMinutes
                                Session = None
                                BodySha256 = sha256 body
                            }
                    )
                | _ -> Error "historical-claim-field-value"
            | [ Some("worker", worker); Some("lease", lease); Some("session", session) ] ->
                match parsePositiveCanonicalInt lease with
                | Some leaseMinutes
                    when not (String.IsNullOrWhiteSpace worker)
                         && not (String.IsNullOrWhiteSpace session) ->
                    Ok(
                        Some
                            {
                                Worker = worker
                                LeaseMinutes = leaseMinutes
                                Session = Some session
                                BodySha256 = sha256 body
                            }
                    )
                | _ -> Error "historical-claim-field-value"
            | _ -> Error "historical-claim-field-shape"
