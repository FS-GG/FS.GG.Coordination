# GS2-09.9 nested native repository identity

Status: source-only, non-authorizing candidate, 2026-09-25. Stacked on draft #822. No protected provider credential, target, grant or installed native effect path is present.

An independent red-before lost-response test issued one synthetic POST. Two complete native PR readbacks had the selected head repository numeric ID and `full_name` but nested `owner.login` was `Foreign`; the classifier returned `ExactPull`. The returned repository object contradicted its own selected name, so the native census was not coherent.

The native reader now checks each listed and detailed head/base repository object's numeric ID, safe full name, and any present `name`, `owner.login` and REST URL for internal agreement. It performs that check for every PR row, including rows whose body marker is unrelated to this operation. The classifier applies the same check to the selected PR. Focused controls cover foreign head/base owner or name, an unrelated PR with contradictory nested identity, and coherent optional owner/name fields. A lost response remains eligible only when both native reads are complete and exact.

The external provisional native-source SHA-256 is `d8df20ae4f7c690083cc84ca721aef5305aabc0111747416e17dc2f7309142c0`; builder SHA-256 `91ed54c9293639ff8249e219d0ad2af8f0c06ebad2326e47fd2dfd1376e20049`; manifest-file SHA-256 `94638c1280de42373d417994bdfaceb40540797550304d6667828d13ca696baa`. The closed archive remains `d665e9b66f42aa3df8b270b792d5958aec6c4bd2bd136010cd146b5e237dcd19`. These are local source pins for independent review, not approved protected installation bytes.

The #550 owner still must select and read back an integrated runnable artifact/workflow, disposable target and effective App scope, independent reviewer/issuer event, protected grant and CAS generation before authorizing exactly one POST. #545 disputed receipt, disabled workflows, typed gate/index, protected merge, Authority write and cutover remain held.
