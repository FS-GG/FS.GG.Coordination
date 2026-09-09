# GitHub ledger-protection capture

`eng/capture-github-ledger-protection.py` performs the fixed, read-only
GS2-08.2 provider observation. It has no apply operation. The desired policy
binds the two public App and installation identities; the control-issue number
is supplied after that ordinary issue has been created.

## Authentication boundary

The organization-owner `gh` session remains the default transport. It reads
the organization installation inventory and, when its token has the required
user scope, each selected-repository inventory through
`GET /user/installations/{id}/repositories`.

For least-privilege selected-repository readback, pass each Secret Service PEM
on a distinct inherited file descriptor. Never put a PEM, JWT, or installation
token in an argument, environment variable, file, terminal, log, or evidence
record. The capture signs a JWT through OpenSSL using the inherited descriptor,
mints a repository-scoped `contents:read` installation token, calls the fixed
`GET /installation/repositories` endpoint, and discards the JWT and token
response. Only allowlisted repository names and response digests enter output.

The Secret Service attribute names depend on the records created by the
custodian. A custodian-approved invocation has this shape; placeholders are
public selectors, not secret values:

```bash
umask 077
operation_dir="$(mktemp -d)"
exec 3< <(secret-tool lookup SERVICE_ATTRIBUTE SERVICE_VALUE ROLE_ATTRIBUTE ORDINARY_ROLE)
exec 4< <(secret-tool lookup SERVICE_ATTRIBUTE SERVICE_VALUE ROLE_ATTRIBUTE CUTOVER_ROLE)

python3 eng/capture-github-ledger-protection.py \
  --ordinary-app-key-fd 3 \
  --cutover-app-key-fd 4 \
  --output "$operation_dir/prestate-pass1.json"
exec 3<&-
exec 4<&-
exec 3< <(secret-tool lookup SERVICE_ATTRIBUTE SERVICE_VALUE ROLE_ATTRIBUTE ORDINARY_ROLE)
exec 4< <(secret-tool lookup SERVICE_ATTRIBUTE SERVICE_VALUE ROLE_ATTRIBUTE CUTOVER_ROLE)
python3 eng/capture-github-ledger-protection.py \
  --ordinary-app-key-fd 3 \
  --cutover-app-key-fd 4 \
  --previous "$operation_dir/prestate-pass1.json" \
  --output "$operation_dir/prestate-pass2.json"

exec 3<&-
exec 4<&-
```

Secret Service lookups return a one-shot pipe, so each capture opens fresh
descriptors. Do not copy key bytes to make a descriptor reusable.

Every capture output is forced to mode `0600`. A missing key descriptor falls
back to the existing user-token endpoint; denial, missing pagination, a wrong
installation id, an unexpected repository set, or token-mint failure becomes a
gap and returns exit code 2.

Environment normalization requires exactly one `required_reviewers` protection
rule. Reviewer identities and `prevent_self_review` are read from that rule,
not from the environment root. A missing or duplicate rule, a non-Boolean
`prevent_self_review`, or an unreadable reviewer id makes the environment
resource unknown and the capture fail closed.

## Binding the control issue and sealing a dry plan

After the ordinary, non-PR Authority control issue exists, supply its positive
number to both passes:

```bash
python3 eng/capture-github-ledger-protection.py \
  --ordinary-app-key-fd 3 --cutover-app-key-fd 4 \
  --control-issue-number CONTROL_ISSUE_NUMBER \
  --output "$operation_dir/prestate-pass1.json"
python3 eng/capture-github-ledger-protection.py \
  --ordinary-app-key-fd 3 --cutover-app-key-fd 4 \
  --control-issue-number CONTROL_ISSUE_NUMBER \
  --previous "$operation_dir/prestate-pass1.json" \
  --output "$operation_dir/prestate-pass2.json"
```

The capture verifies that the number identifies an issue rather than a pull
request. Pass two also requires the complete normalized provider set and every
public binding to equal pass one. Changing an App id, installation id, or issue
number produces `binding-drift`. The codec then binds the ordinary and cutover
Apps plus control issue into conformance and the deterministic plan seal.

This yields a sealed dry plan only: `applyAuthorized` remains false and
`writesAttempted` remains zero. A separate, fresh authorization must bind that
exact plan seal before any GitHub setting changes.
