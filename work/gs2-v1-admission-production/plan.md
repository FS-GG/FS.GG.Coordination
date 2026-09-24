# Production v1 admission repair

The accepted GS2-08 bridge is fail-closed in production. On 2026-09-23 the protected fleet cutover ref existed, while the canonical `fleet-v1-admission:fs-gg-production` operation ref returned 404. The ordinary CLI must not infer an operation scope, use an ambient token as protected authority, or initialize this ref.

## Formal constraint

Keep the authored `src/FS.GG.Coordination.Protocol/Protocol.md` unchanged. The relevant gates are `protocolEnvelopesAreValidAndOrdered`, `durableProtocolCheckpointsArePreserved`, `mutationResultsAreBound`, `uncertainMutationOutcomesStayUnknown`, and `durablePlansAreOrderedAndResumable`. A failed invariant stops implementation; it is not repaired by weakening the specification.

## Sequence

1. Coordination `V1AdmissionRegistry.planGenesis`: derive exact, replayable root Git objects only from a verified `OperatingV1` authority and two absent journal heads. No provider write or authorization is supplied by this plan. `verifyGenesisReadback` requires the exact planned root. Unit controls reject existing, moved, incomplete, unauthorized and non-`OperatingV1` observations. **Source complete; the unchanged canonical Quint protocol qualified.**
2. Separately protected operation: bind the genesis plan to exact source, current authority/manifest, effective rules, dedicated writer identity and explicit human approval; recheck absence immediately before one expected-absent ref creation, then independently read back every object and the ref. A source plan or chat authorization alone cannot create the ref. The trust-anchored RSA-PSS signature verifier binds exact genesis bytes, source/workflow identity, protected run ID and expiry. The native-approval verifier checks the exact workflow SHA-256, run, artifact and effective environment rules. Under `.github` ADR-0087 the dedicated `fleet-v1-admission-owner` environment allows its sole accountable owner to initiate and approve after a five-minute wait; the shared `fleet-cutover` environment remains unchanged. The protected installer core, absent-state collector, and exact installed-state readback collector/decoder have fake-provider tests. The readback requires the expected ref, parentless commit, exact raw objects and stable ref census. The ordinary-App provider transport verifies an App JWT issued outside the container, App/installation identity, token repository and permission scope, exact objects and the canonical admission ref. A typed port binding routes `ReadRegistry` through fresh native evidence on every call, selecting strict absence or exact installed-state readback from a native ref observation; it also bounds reuse of the initial authority objects to two minutes and rereads the cutover head. A new process runner joins these adapters to the installer without accepting a PEM or ambient writer token. A Main-only Secret Service helper can issue the bounded JWT and sign the exact public payload using anonymous in-memory descriptors, after its own fresh native approval read. The separate stdio provider bridge binds the planned Git objects and canonical ref. Synthetic-key/fake-provider tests and the unchanged base Quint protocol pass; live review and protected execution are still required. Source and workflow are in draft PRs `FS.GG.Coordination#500` and `.github#3659`, not merged or dispatched. The validated intake draft is `dotgithub-workflow-intake.json`; `intake apply` still refuses because production v1 admission itself is unavailable.
3. Protected admission service: inventory incumbent operation and claim generations, validate each `MutationContext` against the fresh fleet authority, and append admissions under exact-parent CAS. The ordinary CLI may recover only these already-admitted handles; it cannot invent an admission from argv.
4. Producer: implement live raw-Git authority/journal readers, exact-parent CAS writer, durable operation-scope composition and provider reconciliation. Keep `UnavailableProductionMutationFence` until all inputs and controls are installed. Prove unknown response, lost append, concurrent parent, stale epoch, retry, and old-client refusals against independent fake providers.
5. Publish/adopt a coherent producer and verify installed runtime behavior with the existing guarded intake. Only then resume GS2-09.7 filing and protected acceptance; do not infer migration acceptance from this repair.

The source-ancestry reader collects a stable Coordination `main` ref, exact source commit/tree, and native compare/merge-base evidence; its strict decoder rejects a stale or contradictory observation. This remains a read-only input to the installer, not a source-plan approval.

The current step does not install the journal, enable v1 writes, or satisfy the remaining roadmap gates.

The 2026-09-23 live read-only probe was refreshed during the install attempt. It again verified Authority commit `42a25b1480203207183f37c56d315c4161fb627b`, observed the canonical operation ref absent, and derived planned genesis commit `34f1ea8796e5a0bef515705c81a92fcb62620928`. This is an expiring observation, not an authorization or a write receipt. Recollect immediately before any protected installation.

The revised single-owner intake draft validates under `gs2-v1-admission-single-owner-workflow-20260923`, but `intake apply` refuses at the production v1 admission guard; no issue or board item was created. The prior draft id has a durable intake intent, so its changed policy cannot be reused under that id. Existing `.github` release workflows consume `secrets.FSGG_ORDINARY_LEDGER_APP_PRIVATE_KEY`, `vars.FSGG_ORDINARY_LEDGER_APP_ID`, and `secrets.FSGG_ORDINARY_LEDGER_INSTALLATION_ID` for ordinary App 4882140; the key may be an organization Actions secret restricted to `.github`. Repository/environment secret listings do not reveal it, organization secret listing returns 403, and the authenticated user token is not an App JWT. The next protected write requires the existing App credential and pinned signer through approved custody, plus a live installer-port adapter. Do not substitute the user token or a newly generated unanchored signer.

On 2026-09-23 SystemAdmin reported a Main-host KDE Secret Service custody check: the ordinary App 4882140 record produced a JWT accepted by GitHub `/app`, and the separate 3072-bit RSA authorizer record's public SPKI SHA-256 matched the installed fleet trust anchor (`54568140db351fbc525043601cec0ecbffda12e235436d44d68bed10f14fb7d6`). No private bytes were moved into this worktree or container. This is custody evidence, not the signed protected-operation approval or permission to create the ref. The import-only provider transport can accept a short-lived App JWT through an inherited descriptor; a reviewed host-side issuer or approved Actions secret consumer must still supply it, and the authorizer signature must be produced on Main.

## Current implementation gate

- Provider transport, typed readback port binding, source-ancestry collector/decoder, and exact protection snapshot/decoder: done locally; nine provider controls, four source-collector controls, 31 focused F# controls, full solution build, 474 unit tests, Q6 operational validation, and four canonical Quint base gates pass. The source reader requires stable main heads and an exact compare/merge-base binding; a diverged draft source is not promoted to merged authority. The protection reader requires visible bypass actors, two stable ruleset/effective-rule reads, and the scoped ordinary-App token. A read-only live API-shape probe parsed writer `21872113`, integrity `21872115`, and the four effective admission-branch rules; it used a fake App identity and is not a live credential proof.
- Native evidence decoders are joined to the typed installer port. The process-level runner and Main-side credential/signing helper are implemented locally, but not merged, installed on Main, or exercised with real credentials.
- The typed port now accepts raw native source, effective-rule/credential, and protected-approval evidence callbacks and decodes each fresh response when the installer asks for it. Fake readers prove an earlier success is not cached across malformed or stale observations, and a native approval for the wrong run is refused. This is source-only adapter progress; it does not mint an App token, sign an intent, dispatch a workflow, or create a journal ref.
- Verification after the raw-reader binding: full solution build with zero warnings, 474 unit tests, nine provider and four source-collector controls, Q6 operational gate, and unchanged canonical Quint base qualification (eight positive invariants, 71 negative controls, no counterexamples) pass.
- A strict, bounded signature-envelope decoder now gives a Main-side signer a public-only handoff shape: exact plan intent SHA-256, key id, protected run id, bounded timestamps, public-key PEM, and detached base64 signature. It rejects private-key PEM, duplicate/extra fields, changed intent, and malformed encoding. The installer still performs independent trust-anchor RSA-PSS verification and all fresh native preflights; this envelope is neither a signer nor an approval.
- Verification after the envelope decoder: full solution build with zero warnings, 474 unit tests, and unchanged canonical Quint base qualification pass; the fake altered-signature control parses but fails trust verification as intended.
- The process runner has `prepare`, `signing-payload`, and `apply` modes. It requires an exact merged source revision, an exact workflow revision on `.github/main` whose bytes match the pinned digest, and an exact public trust anchor; `apply` takes only a short-lived App JWT over inherited FD 3. The Main helper reads the wallet with the exact ordinary App and authorizer attributes, verifies native run/environment/owner/receipt binding, and emits a public signature envelope or a JWT over stdout. Neither tool accepts a private key as an argument or writes one to disk. Three synthetic-key Main controls and three fake bridge controls pass. The runner script compiled under FSI in a refusal smoke check. These are source tests, not a live deployment receipt.
- `intake validate` passed, but `intake apply` fails closed at the missing production admission guard. It created no issue or board record. The accepted `.github` ADR-0087 and draft workflow PR remain the protected bootstrap control-plane route; do not claim the intake succeeded.
- Next: land the reviewed workflow and source, obtain the public installed trust-anchor bytes on Main, rerun `prepare` against merged revisions, dispatch the pinned workflow on `main`, wait for native owner approval and successful receipt, then run `signing-payload`, Main `sign-intent` and `issue-jwt`, and `apply` through the narrow pipe. Perform independent readback of the canonical operation ref and all exact objects. If any read, credential or approval gate fails or the write result is unknown, stop and reconcile without creating a second ref.
- Still missing: source/workflow merge, Main host execution channel, protected dispatch, final live readback. No protected write is authorized by this status alone.

## 2026-09-23 source-only continuation

This section supersedes the earlier draft/merge status above. The protected workflow is merged on `.github/main` by PR #3659 at `d6ee7c79d67c3bdc2e3af07dcbe0e606967f76b5`; its installed file SHA-256 is `07435f26a2e22b6bd597aa89ce83192b39c8ab19d7aeabd74c9a67e16be4adf3`. The genesis installer, process runner, Main-only credential/signing helper, and synthetic controls are merged on Coordination `main` by PR #500 at `ca8fb67b56f805457099b9af846aa2f8f5ba0a91`. Neither merge dispatched the protected workflow or created the admission journal.

The copied public GS2-08.2 trust anchor has SHA-256 `cd5d20ddb4469873e4f40a88e17e2c64321f2038638b76de452298d58a4978e5`, while the live `OperatingV1` cutover requires `0a9f84f72ca10c01b5acc386a32ce6920fea15231f90f65a8f17df9e87d9a779`. Read-only `prepare` refused on this mismatch. The correct public file is currently unavailable; do not substitute the copied file, invoke Main signing, dispatch the workflow, or attempt a protected journal write. Recollect authority and approval evidence when the correct file arrives.

Source-only step 3 is merged on Coordination `main` by PR #502 at `8d04536becb6e870202b79a93a51bfd206ac6e13`: the dormant admission service validates a durable journal and fresh authority, plans an exact-parent append, rereads authority, and returns a handle only after reconciled durable readback. A separate source branch adds a bounded native journal-history collector, strict typed restore, and a read-only port; failure remains unreadable, never an absent journal, and writes are refused. These source slices do not remove the ordinary CLI production fence. The exact-parent production CAS writer, claim-journal reader, provider reconciliation, and installed runtime acceptance remain open.

## 2026-09-24 read and CAS continuation

The read-only native journal collector and strict typed restore are merged by PR #503 at `c00179a7c7305cee03e6d7e84112dd25a0d16c61`. The public typed CAS envelope and local bare-Git expected-parent fixture are merged by PR #504 at `75fc2e2fb714255b73f6f269a072b3d245871edb`. The fixture refuses network remotes and has no credential path. A conservative source-only port now joins fresh native reads to a future writer callback while mapping every raw write response to unknown until exact durable readback; unreadable preflight and unreadable conflict reread are indeterminate, not parent conflict. This does not install the journal or lift the ordinary CLI production fence. The scoped production receive-pack credential path, claim-journal reader, provider reconciliation, and installed runtime acceptance remain open. The copied public trust anchor still does not match live `OperatingV1`; no protected dispatch or write has occurred.

## 2026-09-24 scoped CAS source continuation

An import-only receive-pack path now validates the fixed-repository public CAS
envelope, rechecks effective journal protection rules and ordinary-App scope,
fetches the exact parent, stages only the validated objects, and pushes the
canonical operation ref under an exact expected-parent lease. Its installation
token reaches Git only through an anonymous descriptor and fixed askpass helper;
Git receives a minimal environment with ambient credential helpers and hooks
excluded. Even a successful Git response remains unknown to the typed port until
fresh independent journal readback. Fake-provider, local competing-parent,
credential-prompt, Q6, all 690 architecture controls, and the unchanged full
canonical Quint qualification pass. This is source only: no approved
per-admission JWT issuer/process composition, claim-journal reader, provider
reconciliation, installed runtime acceptance, or live write exists. The ordinary
CLI production fence stays in place. The incorrect public trust anchor still
blocks protected genesis installation.

## 2026-09-24 current-anchor read-only handoff

This section supersedes the earlier statements that the current public trust
anchor is unavailable. The exact public `OperatingV1` anchor is readable at
`/home/developer/.local/share/fs-gg-public/ledger-protection/operating-v1-trust-anchor.json`;
its SHA-256 is `0a9f84f72ca10c01b5acc386a32ce6920fea15231f90f65a8f17df9e87d9a779`.
It pins authorizer public SPKI SHA-256
`2dc8d29f8d5a675d070701dacd6dacf2ca3e368ecf823fa9eaf560ddd22d77be`.
The older `trust-anchor.json` is unchanged and must not be substituted.

The read-only `eng/run-v1-admission-genesis.fsx prepare` check passed on
Coordination `main` source commit `4f8b7f7b7acb22bfa2cc80a7752f6cc3a185c1b4`
(tree `80547c83d176873c55de8cf21ead6a187393e66d`) against `.github/main`
workflow revision `d6ee7c79d67c3bdc2e3af07dcbe0e606967f76b5`.
It produced genesis intent SHA-256
`e32bc9f6ddc547fb8eb57a643a3dc9a47d82d6a1fa42d439ca0d9e6cefee8f86`
and expected genesis commit `34f1ea8796e5a0bef515705c81a92fcb62620928`.
An independent `git ls-remote` of the canonical admission operation ref returned
no ref. No protected workflow was dispatched and no key, JWT, token, signature,
or journal write was used in this check.

Reported custody evidence, not locally verified in this container: the matching
authorizer private key remains on Work. The older Main wallet authorizer
fingerprint `54568140db351fbc525043601cec0ecbffda12e235436d44d68bed10f14fb7d6`
does not match the current anchor and must not sign. The Main-only helper has no
reviewed narrow Work-host signing route yet. Source-only work may continue in
parallel; protected genesis requires (1) a reviewed Work-host custody/signing
interface that returns only a public signature envelope after fresh native
approval, (2) the separately approved protected workflow and ordinary-App
credential handoff, and (3) exact expected-absent installation with independent
durable readback. Custody evidence and this read-only plan are not approval.

The handoff draft `work-host-signer-handoff-intake.json` validates, but
`fsgg-coord intake apply` refused its production v1 admission room read because
the journal is not installed. A fresh GitHub issue search returned zero matches;
no issue was created and no Main receipt can be claimed. Use an explicitly
approved alternate channel or restore the governed intake before treating the
request as delivered.
