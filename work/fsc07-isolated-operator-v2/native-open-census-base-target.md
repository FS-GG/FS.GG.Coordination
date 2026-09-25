# GS2-09.9 open PR census base target

Status: source-only, non-authorizing candidate, 2026-09-25. Stacked on draft #832. The v2 operator has no protected selected target, credential, grant or installed native effect path.

An independent red-before lost-response test issued one synthetic POST. Both native open-PR reads contained the selected exact PR and an unrelated PR whose base repository was `FS-GG/foreign`, even though the request was for `FS-GG/disposable`. The nested foreign repository object was internally coherent, so the reader skipped the unrelated marker and returned `ExactPull`. A PR row returned for the selected repository cannot target another base repository while the census claims completeness.

The native reader now requires the base repository of every listed and detailed PR row to match the selected repository's numeric ID and full name. Heads from forks remain eligible for unrelated rows. Focused controls cover a foreign base name, a foreign numeric ID with the selected name, a valid fork head, and a lost-response one-POST false green. The selected PR retains its stricter exact head/base and marker checks.

The provisional external native-source SHA-256 is `f79f4e2e84329df980dc1339dd77e7fc19b9cd86977bffd42f67be74d3ce7581`; builder SHA-256 `9ae5f59102ff276875e2b57777628463db7e9081860ccf3268d713eba03a1298`; manifest-file SHA-256 `332ad7eb14976841bb91273556c860ed2cc434ce93f03e338e233f4f93cfa9e5`. The closed archive stays `d665e9b66f42aa3df8b270b792d5958aec6c4bd2bd136010cd146b5e237dcd19`. These local pins require independent protected review before installation.

The #550 integrated runnable artifact/workflow, target and effective App scope, independent reviewer/issuer, protected CAS/grant and one-POST admission remain unresolved. #545 disputed receipt, disabled workflows, typed gate/index, protected merge, Authority write and cutover stay held.
