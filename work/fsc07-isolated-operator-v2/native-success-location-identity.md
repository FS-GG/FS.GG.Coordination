# GS2-09.9 successful native response Location identity

Status: source-only, non-authorizing candidate, 2026-09-25. Stacked on draft #820. The provisional v2 operator and closed effect archive have no protected provider credential or installed dispatch path.

An independent red-before offline test issued one synthetic POST and received HTTP 201 with a complete PR #8 body but a `Location` header naming PR #9. Both complete native reads identified PR #8; the prior classifier still returned `ExactPull(number=8)`. A successful response that names two PR URLs cannot identify the result of the one POST.

For a 2xx response, the classifier now validates response header shape and refuses a `Location` value other than the canonical selected PR REST URL, duplicate `Location` fields, or a malformed header pair. Absence of `Location` remains valid. A matching case stays exact. Lost responses and 5xx still use the complete stable native readback; explicit 3xx/4xx remains Unknown. The focused controls count one synthetic POST and include foreign, matching, duplicate and query-alias URL cases. No provider body, credential or exception text is returned.

The external native-source SHA-256 is `5dea86000e990e66884b5e203687b8ff241c56b8197d1e5625d9f54cc1c16d02`; the closed scaffold builder SHA-256 is `51753b3b773437db34453ab1778b41817a7beda6851199a228fe5cab46733de0`; the manifest-file SHA-256 is `3f000d8e60f5ab8a13d4d5d8289d1963ef9661acef232b9e0e35c9cfa72d5acf`. The closed archive remains `d665e9b66f42aa3df8b270b792d5958aec6c4bd2bd136010cd146b5e237dcd19`. These hashes are provisional local source pins, not protected approval.

The independent owner still must select and read back the integrated runnable artifact/workflow, protected target and App scope, reviewer and issuer event, grant and CAS generation before the #550 one-POST window. #545 disputed receipt, both disabled workflows, gate/index, protected merge, Authority write and cutover stay held.
