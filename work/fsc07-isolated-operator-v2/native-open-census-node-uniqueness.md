# GS2-09.9 open PR census node identity uniqueness

Status: source-only, non-authorizing candidate, 2026-09-25. Stacked on draft #834. The v2 operator has no protected target, token, grant or installed native effect path.

An independent red-before lost-response test issued one synthetic POST. Both complete native open-PR reads returned the selected exact PR number 8 and an unrelated PR number 9 with the same `node_id: PR_8`. The reader checked unique numbers and list/detail agreement but skipped the unrelated marker, then returned `ExactPull`. Two distinct PR numbers cannot identify the same provider PR node in a complete census.

The native reader now requires every listed PR row to have a nonempty string node ID unique across the entire selected repository census before marker selection. Its existing list/detail equality check still binds each row's node ID to its detail. Focused controls cover one lost-response POST, duplicate and empty node IDs, and a valid unrelated PR with its own node ID.

The provisional external native-source SHA-256 is `7f275d4f6e28808a6d30b9427dc26d7a867fdddbd65ae146d5b28872c1a72fcd`; builder SHA-256 `3e4ade0483d9fa2c71317752384940a4c079ed4d911eddaac89dbc3ff5d95720`; manifest-file SHA-256 `8caaf94d749e978118c1a85a95812f3ed52383bb965ade29fa552cffc35bac45`. The closed archive stays `d665e9b66f42aa3df8b270b792d5958aec6c4bd2bd136010cd146b5e237dcd19`. These draft hashes require independent protected review before installation.

The #550 integrated runnable artifact/workflow, selected target and effective App scope, independent reviewer/issuer, protected CAS/grant and one-POST admission remain unresolved. #545 disputed receipt, disabled workflows, typed gate/index, protected merge, Authority write and cutover stay held.
