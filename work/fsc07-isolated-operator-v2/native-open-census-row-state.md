# GS2-09.9 complete open PR census row state

Status: source-only, non-authorizing candidate, 2026-09-25. Stacked on draft #830. The v2 operator still has no protected provider credential or installed dispatch path.

An independent red-before lost-response test issued one synthetic POST. Each native read returned the selected exact open PR plus an unrelated row with `state: closed` from `GET pulls?state=open`. The reader skipped the unrelated marker, called the census complete and returned `ExactPull`. A contradictory row on an open-only page makes the whole census unqualified, even when the selected row is exact.

The native reader now requires every listed and detailed PR row to have `state: open` and a boolean `draft`. A present `merged` must be false and a present `merged_at` must be null. These checks run before marker selection. Focused controls cover a closed unrelated row after one lost-response POST, numeric draft, merged and merged-at contradictions, and an unrelated coherent open row that leaves the selected exact PR eligible. The repair does not retry or perform a live provider write.

The provisional external native-source SHA-256 is `125f06550788b4b6cc6416e3644ea1e31ea0124aeee93ef9d0ac8cb196c13b14`; builder SHA-256 `090d03ce34255010927a351e731a568bd621e596a3ff92e3bfc06a3624716eda`; manifest-file SHA-256 `c25ee60b81fd1ce316a593f363afd835cc2f864ce81a0e20db80f2e65121e832`. The closed archive stays `d665e9b66f42aa3df8b270b792d5958aec6c4bd2bd136010cd146b5e237dcd19`. These draft hashes do not establish a protected release or native effect.

The #550 integrated runnable artifact/workflow, selected target/App scope, independent reviewer/issuer, protected CAS/grant and one-POST admission remain unresolved. #545 disputed receipt, disabled workflows, typed gate/index, protected merge, Authority write and cutover stay held.
