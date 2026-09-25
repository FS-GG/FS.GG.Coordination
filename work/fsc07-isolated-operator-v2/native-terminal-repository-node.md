# GS2-09.9 terminal native repository node identity

Status: source-only, non-authorizing candidate, 2026-09-25. Stacked on draft #827. No protected provider target, token, grant, journal or installed effect is selected.

An independent red-before lost-response test issued one synthetic POST. Each complete PR probe read the selected numeric repository ID at its start and end, but the provider `node_id` changed from `R_44_A` to `R_44_B` between those reads. Repeating the same drift in both probes gave the same transcript hash, so the prior classifier returned `ExactPull`. Two reproducible transcripts cannot make a drifting target identity stable.

The native pull and protection readers now retain the initial repository `node_id` and require its terminal value to match within each probe. A present ID must be a nonempty string. Focused tests cover lost-response PR drift with one counted POST, protection drift with one counted PUT, stable node identity, missing terminal node, and malformed node type. Existing synthetic fixtures without a node ID remain limited to numeric repository identity. A future protected plan must independently bind the selected target's numeric and node IDs in its installed reader and grant; this local repair does not supply that fact.

The provisional external native-source SHA-256 is `deaf6fdd9d466128f6bf54729fb0e59514bebef04cc4bdc6fd4d56dae65b1a64`; builder SHA-256 `0fa9ec13bea6453482f75c3a07cf878ad2c86a4e026a48f89dcce584326590ab`; manifest-file SHA-256 `12774b564e950b6381ae96aa81f5df93db0c0d35e8d41046f1223ac1ba3a3e4d`. The closed scaffold archive remains `d665e9b66f42aa3df8b270b792d5958aec6c4bd2bd136010cd146b5e237dcd19`. These hashes require independent protected review before any installation.

The #550 target/App/reviewer/issuer/CAS/grant and one-POST admission, #545 disputed receipt, both disabled workflows, typed gate/index, protected merge, Authority write and cutover stay held.
