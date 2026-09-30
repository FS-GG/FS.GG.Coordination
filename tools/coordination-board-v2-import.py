#!/usr/bin/env python3
"""Fixed, metered Coordination V2 creation and representative pilot operation.

The command has no caller-selected owner, project, fields or issues.  ``preflight``
is read-only.  ``execute`` is a protected effect and requires a durable local state
file plus the exact reviewed admission SHA supplied by the admitting workflow.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import pathlib
import sys
import urllib.error
import urllib.request

OWNER = "FS-GG"
TITLE = "Coordination V2"
LEGACY_ID = "PVT_kwDOEYAWY84Bb08W"
MANIFEST_HEAD = "ee809452e32e2745a37626a3d1ab114ef914e0c3"
MANIFEST_SHA256 = "10d6e84ebf1c33e75d5e0e2d2c25c43c0d6b893dc12d14a145ba51842a029e35"
MARKER = f"fsgg:coordination-board-v2/v1 manifest={MANIFEST_HEAD}"
GRAPHQL = "https://api.github.com/graphql"
MINIMUM_REMAINING = 8

PILOT = (
    ("FS-GG/.github#2963", "I_kwDOS6feoM8AAAABOKI1XA", "2026-09-25T04:58:08Z", "In progress", "docs/github-substrate-v2-roadmap.md", "Active delivery", "Verified", (("FS-GG/FS.GG.SDD#924", "closed"),)),
    ("FS-GG/.github#2995", "I_kwDOS6feoM8AAAABOP7vTw", "2026-09-04T07:21:22Z", "Blocked", "docs/coordination/2026-08-25-quint-first-typed-sdd-migration-design.md", "Active delivery", "Verified", (("FS-GG/FS.GG.SDD#924", "closed"), ("FS-GG/.github#2963", "open"))),
    ("FS-GG/FS.GG.Coordination#24", "I_kwDOUEVTys8AAAABOdY3ag", "2026-08-28T13:30:19Z", "Blocked", "docs/github-substrate-v2-roadmap.md", "Active delivery", "Verified", (("FS-GG/.github#2963", "open"),)),
)

FIELDS = {
    "Status": ("SINGLE_SELECT", ("Backlog", "Ready", "In progress", "Blocked", "Done")),
    "Roadmap": ("TEXT", ()),
    "Track": ("SINGLE_SELECT", ("Active delivery", "Follow-up")),
    "Observation": ("SINGLE_SELECT", ("Verified", "Stale", "Unknown")),
}

INSPECT = """query BoardV2ImportInspect($owner:String!,$issue0:ID!,$issue1:ID!,$issue2:ID!){
  viewer{login}
  organization(login:$owner){id login projectsV2(first:100){totalCount pageInfo{hasNextPage endCursor} nodes{
    id number title shortDescription closed createdAt creator{login}
    fields(first:100){totalCount pageInfo{hasNextPage endCursor} nodes{
      __typename ... on ProjectV2FieldCommon{id name dataType}
      ... on ProjectV2SingleSelectField{id name dataType options{id name}}
    }}
    items(first:100){totalCount pageInfo{hasNextPage endCursor} nodes{
      id content{... on Issue{id number updatedAt state repository{nameWithOwner}}}
      fieldValues(first:20){totalCount pageInfo{hasNextPage endCursor} nodes{
        __typename ... on ProjectV2ItemFieldSingleSelectValue{field{... on ProjectV2SingleSelectField{id name}} optionId name}
        ... on ProjectV2ItemFieldTextValue{field{... on ProjectV2Field{id name}} text}
      }}
    }}
  }}}
  nodes(ids:[$issue0,$issue1,$issue2]){... on Issue{id number updatedAt state repository{nameWithOwner}}}
  rateLimit{limit remaining resetAt cost}
}"""

CREATE = """mutation BoardV2Create($owner:ID!){createProjectV2(input:{ownerId:$owner,title:\"Coordination V2\",clientMutationId:\"coord-board-v2-create-v1\"}){projectV2{id number title}}}"""


def add_mutation(rows):
    declarations = ["$project:ID!"]
    aliases = []
    values = {}
    for row in rows:
        index = PILOT.index(row)
        declarations.append(f"$issue{index}:ID!")
        values[f"issue{index}"] = row[1]
        aliases.append(f"i{index}:addProjectV2ItemById(input:{{projectId:$project,contentId:$issue{index},clientMutationId:\"coord-board-v2-add-{index}-v1\"}}){{item{{id}}}}")
    return "mutation BoardV2PilotAdd(" + ",".join(declarations) + "){" + " ".join(aliases) + "}", values


class Refusal(Exception):
    pass


def _need(value, message):
    if not value:
        raise Refusal(message)


class Client:
    def __init__(self, token: str, sender=None):
        self.token = token
        self.sender = sender or self._send
        self.remaining = None

    def _send(self, document, variables):
        body = json.dumps({"query": document, "variables": variables}, separators=(",", ":")).encode()
        request = urllib.request.Request(GRAPHQL, data=body, method="POST", headers={
            "Accept": "application/vnd.github+json", "Authorization": f"Bearer {self.token}",
            "Content-Type": "application/json", "User-Agent": "fsgg-coordination-board-v2/1",
            "X-GitHub-Api-Version": "2022-11-28",
        })
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return response.status, json.loads(response.read())
        except urllib.error.HTTPError as error:
            try:
                payload = json.loads(error.read())
            except (ValueError, OSError):
                payload = {}
            return error.code, payload
        except (OSError, ValueError) as error:
            raise Refusal(f"transport-unavailable:{type(error).__name__}") from error

    def call(self, document, variables, *, mutation=False):
        if mutation:
            _need(self.remaining is not None, "mutation-refused:missing-metered-preflight")
            _need(self.remaining > 0, "mutation-refused:rate-budget-exhausted")
        status, payload = self.sender(document, variables)
        _need(status == 200, f"github-refused:{status}")
        _need(isinstance(payload, dict), "malformed-response")
        errors = payload.get("errors")
        _need(not errors, "graphql-errors:" + json.dumps(errors, separators=(",", ":")))
        data = payload.get("data")
        _need(isinstance(data, dict), "malformed-data")
        if mutation:
            self.remaining -= 1
        else:
            budget = data.get("rateLimit")
            _need(isinstance(budget, dict), "missing-rate-budget")
            for name in ("limit", "remaining", "cost"):
                _need(isinstance(budget.get(name), int) and budget[name] >= 0, f"invalid-rate-budget:{name}")
            _need(isinstance(budget.get("resetAt"), str), "invalid-rate-budget:resetAt")
            self.remaining = budget["remaining"]
        return data


def variables():
    return {"owner": OWNER, **{f"issue{i}": row[1] for i, row in enumerate(PILOT)}}


def _page(connection, name):
    _need(isinstance(connection, dict), f"missing-{name}")
    page = connection.get("pageInfo")
    _need(isinstance(page, dict) and page.get("hasNextPage") is False, f"incomplete-{name}")
    nodes = connection.get("nodes")
    _need(isinstance(nodes, list) and connection.get("totalCount") == len(nodes), f"incomplete-{name}-count")
    return nodes


def inspect(client: Client):
    data = client.call(INSPECT, variables())
    viewer = data.get("viewer")
    org = data.get("organization")
    _need(isinstance(viewer, dict) and isinstance(viewer.get("login"), str), "viewer-unreadable")
    _need(isinstance(org, dict) and org.get("login") == OWNER and isinstance(org.get("id"), str), "organization-unreadable")
    projects = _page(org.get("projectsV2"), "projects")
    matches = [p for p in projects if isinstance(p, dict) and p.get("title") == TITLE]
    _need(len(matches) <= 1, "duplicate-target-project")
    _need(all(p.get("id") != LEGACY_ID and p.get("number") != 1 for p in matches), "legacy-project-mutation-refused")
    observed = data.get("nodes")
    _need(isinstance(observed, list) and len(observed) == len(PILOT), "pilot-source-incomplete")
    by_id = {item.get("id"): item for item in observed if isinstance(item, dict)}
    _need(len(by_id) == len(PILOT), "pilot-source-duplicate")
    for ref, node, updated, *_ in PILOT:
        issue = by_id.get(node)
        _need(issue is not None, f"pilot-source-missing:{ref}")
        _need(issue.get("updatedAt") == updated, f"pilot-source-stale:{ref}")
        _need(issue.get("state", "").lower() == "open", f"pilot-source-not-open:{ref}")
    return {"viewer": viewer["login"], "ownerId": org["id"], "project": matches[0] if matches else None,
            "rateLimit": data["rateLimit"]}


def field_map(project):
    nodes = _page(project.get("fields"), "fields")
    selected = {}
    for field in nodes:
        if not isinstance(field, dict) or field.get("name") not in FIELDS:
            continue
        _need(field["name"] not in selected, f"duplicate-field:{field['name']}")
        selected[field["name"]] = field
    return selected


def exact_schema(project):
    selected = field_map(project)
    if set(selected) != set(FIELDS):
        return False
    for name, (kind, options) in FIELDS.items():
        field = selected[name]
        _need(isinstance(field.get("id"), str), f"unknown-field-id:{name}")
        if field.get("dataType") != kind:
            return False
        if options and tuple(option.get("name") for option in field.get("options", [])) != options:
            return False
    return project.get("shortDescription") == MARKER and project.get("closed") is False


def schema_mutation(project):
    selected = field_map(project)
    _need(set(selected).issubset(FIELDS), "unexpected-field-schema")
    aliases = []
    variables = {"project": project["id"]}
    declarations = ["$project:ID!"]
    marker = project.get("shortDescription")
    _need(marker in (None, "", MARKER), "project-marker-drift")
    if marker != MARKER:
        aliases.append(f"marker:updateProjectV2(input:{{projectId:$project,shortDescription:\"{MARKER}\",public:false,clientMutationId:\"coord-board-v2-marker-v1\"}}){{projectV2{{id}}}}")
    status = selected.get("Status")
    _need(status is not None and status.get("dataType") == "SINGLE_SELECT" and isinstance(status.get("id"), str), "status-field-drift")
    if tuple(option.get("name") for option in status.get("options", [])) != FIELDS["Status"][1]:
        declarations.append("$status:ID!"); variables["status"] = status["id"]
        aliases.append("status:updateProjectV2Field(input:{fieldId:$status,singleSelectOptions:[{name:\"Backlog\",color:GRAY,description:\"Not selected for immediate work\"},{name:\"Ready\",color:BLUE,description:\"Dependency-ready scheduling intent\"},{name:\"In progress\",color:YELLOW,description:\"Actively scheduled\"},{name:\"Blocked\",color:RED,description:\"Known unmet dependency\"},{name:\"Done\",color:GREEN,description:\"Evidence-based completion\"}],clientMutationId:\"coord-board-v2-status-v1\"}){projectV2Field{... on ProjectV2FieldCommon{id name}}}")
    definitions = {
        "Roadmap": "roadmap:createProjectV2Field(input:{projectId:$project,dataType:TEXT,name:\"Roadmap\",clientMutationId:\"coord-board-v2-roadmap-v1\"}){projectV2Field{... on ProjectV2FieldCommon{id name}}}",
        "Track": "track:createProjectV2Field(input:{projectId:$project,dataType:SINGLE_SELECT,name:\"Track\",singleSelectOptions:[{name:\"Active delivery\",color:BLUE,description:\"Selected delivery outcome\"},{name:\"Follow-up\",color:PURPLE,description:\"Explicitly selected later observation or decision\"}],clientMutationId:\"coord-board-v2-track-v1\"}){projectV2Field{... on ProjectV2FieldCommon{id name}}}",
        "Observation": "observation:createProjectV2Field(input:{projectId:$project,dataType:SINGLE_SELECT,name:\"Observation\",singleSelectOptions:[{name:\"Verified\",color:GREEN,description:\"Last observation completed\"},{name:\"Stale\",color:YELLOW,description:\"Last verified state is retained but old\"},{name:\"Unknown\",color:GRAY,description:\"Complete observation unavailable\"}],clientMutationId:\"coord-board-v2-observation-v1\"}){projectV2Field{... on ProjectV2FieldCommon{id name}}}",
    }
    for name in ("Roadmap", "Track", "Observation"):
        existing = selected.get(name)
        if existing is None:
            aliases.append(definitions[name])
        else:
            kind, options = FIELDS[name]
            _need(existing.get("dataType") == kind and isinstance(existing.get("id"), str), f"field-drift:{name}")
            if options:
                _need(tuple(option.get("name") for option in existing.get("options", [])) == options, f"field-options-drift:{name}")
    _need(aliases, "schema-mutation-empty")
    return "mutation BoardV2Schema(" + ",".join(declarations) + "){" + " ".join(aliases) + "}", variables


def read_state(path):
    if not path.exists():
        return None
    try:
        value = json.loads(path.read_text())
    except (OSError, ValueError) as error:
        raise Refusal("state-unreadable") from error
    _need(value.get("schema") == "fsgg.coordination-board-v2-operation-state/v1", "state-schema-drift")
    _need(value.get("manifestHead") == MANIFEST_HEAD, "state-manifest-drift")
    return value


def write_state(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + ".pending")
    temp.write_text(json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n")
    os.chmod(temp, 0o600)
    temp.replace(path)


def base_state(stage, **extra):
    return {"schema": "fsgg.coordination-board-v2-operation-state/v1", "manifestHead": MANIFEST_HEAD,
            "stage": stage, **extra}


def validate_bound_project(project, state):
    _need(isinstance(project, dict), "target-project-missing")
    _need(isinstance(project.get("id"), str) and project["id"].startswith("PVT_"), "unknown-project-id")
    _need(project["id"] != LEGACY_ID and project.get("number") != 1, "legacy-project-mutation-refused")
    _need(state.get("projectId") == project["id"], "state-project-identity-drift")


def preflight(client):
    observed = inspect(client)
    return {"schema": "fsgg.coordination-board-v2-preflight/v1", "manifestHead": MANIFEST_HEAD,
            "manifestSha256": MANIFEST_SHA256,
            "owner": OWNER, "title": TITLE, "pilot": [row[0] for row in PILOT],
            "target": None if observed["project"] is None else {"id": observed["project"].get("id"), "number": observed["project"].get("number"), "exactSchema": exact_schema(observed["project"])},
            "viewer": observed["viewer"], "rateLimit": observed["rateLimit"],
            "createCapability": "unknown-not-probed", "effectAuthorized": False}


def execute(client, state_path, admission):
    _need(len(admission) == 40 and all(c in "0123456789abcdef" for c in admission), "invalid-admission-sha")
    observed = inspect(client)
    _need(observed["rateLimit"]["remaining"] >= MINIMUM_REMAINING, "insufficient-rate-budget")
    state = read_state(state_path)
    project = observed["project"]
    now = dt.datetime.now(dt.timezone.utc).isoformat()

    if state is None:
        _need(project is None, "unbound-existing-target-refused")
        state = base_state("create-pending", admissionSha=admission, startedAt=now, ownerId=observed["ownerId"])
        write_state(state_path, state)
        data = client.call(CREATE, {"owner": observed["ownerId"]}, mutation=True)
        created = data.get("createProjectV2", {}).get("projectV2")
        _need(isinstance(created, dict) and created.get("title") == TITLE and created.get("id") != LEGACY_ID, "create-readback-refused")
        state = {**state, "stage": "project-created", "projectId": created.get("id"), "projectNumber": created.get("number")}
        write_state(state_path, state)
        observed = inspect(client)
        project = observed["project"]
    else:
        _need(state.get("admissionSha") == admission, "admission-sha-drift")
        if state.get("stage") == "create-pending":
            if project is None:
                _need(isinstance(state.get("ownerId"), str) and state["ownerId"].startswith("O_"), "state-owner-identity-drift")
                data = client.call(CREATE, {"owner": state.get("ownerId")}, mutation=True)
                created = data.get("createProjectV2", {}).get("projectV2")
                _need(isinstance(created, dict) and created.get("title") == TITLE and created.get("id") != LEGACY_ID, "create-readback-refused")
                state = {**state, "stage": "project-created", "projectId": created.get("id"), "projectNumber": created.get("number")}
                write_state(state_path, state)
                observed = inspect(client); project = observed["project"]
            else:
                _need(project.get("creator", {}).get("login") == observed["viewer"], "uncertain-created-project")
                _need(project.get("shortDescription") in (None, ""), "uncertain-created-project-marker")
                try:
                    created_at = dt.datetime.fromisoformat(project["createdAt"].replace("Z", "+00:00"))
                    started_at = dt.datetime.fromisoformat(state["startedAt"].replace("Z", "+00:00"))
                except (KeyError, TypeError, ValueError) as error:
                    raise Refusal("uncertain-created-project-time") from error
                _need(created_at >= started_at, "uncertain-created-project-time")
                _need(not _page(project.get("items"), "creation-items"), "uncertain-created-project-items")
                _need(set(field_map(project)) == {"Status"}, "uncertain-created-project-fields")
                state = {**state, "stage": "project-created", "projectId": project.get("id"), "projectNumber": project.get("number")}
                write_state(state_path, state)

    validate_bound_project(project, state)
    if not exact_schema(project):
        _need(state.get("stage") in ("project-created", "schema-pending"), "field-drift-refused")
        document, schema_variables = schema_mutation(project)
        state = {**state, "stage": "schema-pending"}
        write_state(state_path, state)
        client.call(document, schema_variables, mutation=True)
        observed = inspect(client); project = observed["project"]
        validate_bound_project(project, state)
        _need(exact_schema(project), "schema-readback-refused")
        state = {**state, "stage": "schema-ready"}; write_state(state_path, state)
    elif state.get("stage") in ("project-created", "schema-pending"):
        state = {**state, "stage": "schema-ready"}; write_state(state_path, state)

    items = _page(project.get("items"), "items")
    content = {}
    for item in items:
        issue = item.get("content") if isinstance(item, dict) else None
        if isinstance(issue, dict) and issue.get("id") in {row[1] for row in PILOT}:
            _need(issue["id"] not in content, f"duplicate-membership:{issue['id']}")
            content[issue["id"]] = item
    missing = [row for row in PILOT if row[1] not in content]
    if missing:
        _need(state.get("stage") in ("schema-ready", "membership-pending"), "membership-stage-refused")
        document, add_variables = add_mutation(missing)
        state = {**state, "stage": "membership-pending"}; write_state(state_path, state)
        client.call(document, {"project": project["id"], **add_variables}, mutation=True)
        observed = inspect(client); project = observed["project"]
        validate_bound_project(project, state)
        items = _page(project.get("items"), "items")
        content = {item.get("content", {}).get("id"): item for item in items if isinstance(item.get("content"), dict)}
        _need(all(row[1] in content for row in PILOT), "membership-readback-refused")

    # Field seeding is emitted only after exact membership and field IDs are known.
    fields = field_map(project)
    aliases, seed_vars = [], {"project": project["id"]}
    expected_values = {}
    for i, row in enumerate(PILOT):
        item = content[row[1]]; seed_vars[f"item{i}"] = item["id"]
        values = (("Status", "singleSelectOptionId", row[3]), ("Roadmap", "text", row[4]),
                  ("Track", "singleSelectOptionId", row[5]), ("Observation", "singleSelectOptionId", row[6]))
        for j, (name, value_kind, wanted) in enumerate(values):
            expected_values[(row[1], name)] = wanted
            seed_vars[f"field{i}_{j}"] = fields[name]["id"]
            if value_kind == "singleSelectOptionId":
                option = next((o for o in fields[name].get("options", []) if o.get("name") == wanted), None)
                _need(option is not None, f"missing-option:{name}:{wanted}")
                seed_vars[f"value{i}_{j}"] = option["id"]
                value = f"{{singleSelectOptionId:$value{i}_{j}}}"
            else:
                seed_vars[f"value{i}_{j}"] = wanted; value = f"{{text:$value{i}_{j}}}"
            aliases.append((row[1], name, i, j, value))

    def observed_values(current_items):
        values = {}
        for current in current_items:
            issue = current.get("content") if isinstance(current, dict) else None
            if not isinstance(issue, dict) or issue.get("id") not in {row[1] for row in PILOT}:
                continue
            for value in _page(current.get("fieldValues"), f"field-values:{issue['id']}"):
                field_value = value.get("field") if isinstance(value, dict) else None
                if not isinstance(field_value, dict) or field_value.get("name") not in FIELDS:
                    continue
                name = field_value["name"]
                actual = value.get("text") if name == "Roadmap" else value.get("name")
                _need((issue["id"], name) not in values, f"duplicate-field-value:{issue['id']}:{name}")
                values[(issue["id"], name)] = actual
        return values

    current_values = observed_values(items)
    disagreements = {key: value for key, value in current_values.items() if expected_values.get(key) != value}
    _need(not disagreements, "pilot-field-drift-refused")
    missing_values = set(expected_values) - set(current_values)
    if missing_values:
        _need(state.get("stage") in ("schema-ready", "membership-pending", "seed-pending"), "pilot-field-stage-refused")
        selected_aliases = [entry for entry in aliases if (entry[0], entry[1]) in missing_values]
        declarations = ["$project:ID!"]
        used_items = sorted({entry[2] for entry in selected_aliases})
        for i in used_items:
            declarations.append(f"$item{i}:ID!")
        mutation_fields = []
        for _, _, i, j, value in selected_aliases:
            declarations.append(f"$field{i}_{j}:ID!")
            declarations.append(f"$value{i}_{j}:{'ID' if j != 1 else 'String'}!")
            mutation_fields.append(f"v{i}_{j}:updateProjectV2ItemFieldValue(input:{{projectId:$project,itemId:$item{i},fieldId:$field{i}_{j},value:{value},clientMutationId:\"coord-board-v2-seed-{i}-{j}-v1\"}}){{projectV2Item{{id}}}}")
        seed_document = "mutation BoardV2PilotSeed(" + ",".join(declarations) + "){" + " ".join(mutation_fields) + "}"
        state = {**state, "stage": "seed-pending"}; write_state(state_path, state)
        client.call(seed_document, seed_vars, mutation=True)
    # A final complete inspect proves identities, pagination and schema. Field values are
    # present in the response and retained by its digest; detailed projection qualification is .3.
    observed = inspect(client); project = observed["project"]
    validate_bound_project(project, state); _need(exact_schema(project), "final-schema-drift")
    final_items = _page(project.get("items"), "items")
    final_ids = [item.get("content", {}).get("id") for item in final_items if isinstance(item.get("content"), dict)]
    _need(all(final_ids.count(row[1]) == 1 for row in PILOT), "final-membership-drift")
    _need(observed_values(final_items) == expected_values, "final-field-readback-refused")
    state = {**state, "stage": "complete", "completedAt": now}; write_state(state_path, state)
    return {"schema": "fsgg.coordination-board-v2-operation-result/v1", "projectId": project["id"],
            "projectNumber": project["number"], "manifestSha256": MANIFEST_SHA256,
            "pilot": [{"issue": row[0], "nodeId": row[1], "dependencies": row[7]} for row in PILOT], "stage": "complete"}


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("preflight")
    run = sub.add_parser("execute")
    run.add_argument("--state", required=True, type=pathlib.Path)
    run.add_argument("--admitted-source", required=True)
    args = parser.parse_args()
    token = os.environ.get("GH_TOKEN", "")
    if not token:
        print("coordination-board-v2-refused:missing-GH_TOKEN", file=sys.stderr); return 2
    try:
        client = Client(token)
        result = preflight(client) if args.command == "preflight" else execute(client, args.state, args.admitted_source)
        print(json.dumps(result, sort_keys=True, separators=(",", ":")))
        return 0
    except Refusal as error:
        print(f"coordination-board-v2-refused:{error}", file=sys.stderr); return 3


if __name__ == "__main__":
    raise SystemExit(main())
