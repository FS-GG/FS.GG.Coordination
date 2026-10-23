#!/usr/bin/env python3
"""Check the maintained source accounting plan without executing FSI or models."""
import argparse
import hashlib
import json
from pathlib import Path
import re


def accounting(source, formal_count, root_count):
    declarations = re.findall(r"^    let preflightMutants =\n        \[\n(.*?)^        \]$", source, re.M | re.S)
    if len(declarations) != 1:
        raise ValueError("expected one declared preflight mutant list")
    rows = declarations[0].splitlines()
    pattern = r'            "([A-Za-z0-9]+)", "([A-Za-z0-9]+)", "([A-Za-z0-9]+)"'
    matches = [re.fullmatch(pattern, row) for row in rows]
    if not matches or any(match is None for match in matches):
        raise ValueError("unsupported mutant declaration")
    mutants = [match.groups() for match in matches]
    if len(set(mutants)) != len(mutants):
        raise ValueError("duplicate mutant declaration")
    required = [
        "    for main, step, invariant in preflightMutants do",
        "    let expectedRejectedProcessCount = 71 + List.length preflightMutants + 5 * declaredFormalTestCount",
        "    if quintRejectedProcessCount - preflightRejectedProcessBaseline <> List.length preflightMutants then",
        "    if quintRejectedProcessCount <> expectedRejectedProcessCount then",
    ]
    for fragment in required:
        if source.count(fragment) != 1:
            raise ValueError("missing or ambiguous source accounting: " + fragment.strip())
    if source.index(required[2]) > source.index("    if preflightOnly then", source.index(required[0])):
        raise ValueError("focused preflight exits before negative census")
    if not isinstance(formal_count, int) or formal_count < 0:
        raise ValueError("invalid formal count")
    positives = re.findall(r'    for main in\n        \[\n(.*?)        \] do\n        let code, output, error =\n            runPreflight', source, re.S)
    replays = re.findall(r'    for main, steps in\n        \[ (.*?) \] do\n        let actualTrace', source)
    if len(positives) != 1 or len(replays) != 1:
        raise ValueError("missing or ambiguous sampling/replay inventory")
    positive_rows = re.findall(r'^            "([A-Za-z0-9]+)"$', positives[0], re.M)
    if len(positive_rows) != len(positives[0].splitlines()) or len(set(positive_rows)) != len(positive_rows):
        raise ValueError("unsupported or duplicate positive inventory")
    replay_rows = [re.fullmatch(r'"([A-Za-z0-9]+)", "([0-9]+)"', row) for row in replays[0].split('; ')]
    if any(row is None for row in replay_rows) or len({row[1] for row in replay_rows}) != len(replay_rows):
        raise ValueError("unsupported or duplicate replay inventory")
    sampling = len(positive_rows) + len(mutants) + len(replay_rows)
    interaction = len(replay_rows)
    inventory = source.split("    // This is the executable qualification plan.", 1)[1].split(
        "    // The unchanged base qualification suite", 1)[0]
    for label, count in [("quint/preflight-sampling", sampling), ("quint/preflight-interaction", interaction)]:
        if inventory.count(f'            "{label}", {count}\n') != 1:
            raise ValueError("declared invocation category differs: " + label)
    external = re.findall(r'^            "external/base", ([0-9]+)$', inventory, re.M)
    base = re.findall(r'\| _ -> ([0-9]+), ([0-9]+)\n\n    for label, count in', inventory)
    if len(external) != 1 or len(base) != 1 or root_count < 1:
        raise ValueError("unsupported base/root invocation plan")
    formal_roles = re.findall(r'^            "quint/(formal-[a-z-]+)", (?:(\d+) \* )?declaredFormalTestCount$', inventory, re.M)
    roles = {name: int(multiplier or 1) for name, multiplier in formal_roles}
    expected_roles = {"formal-simulation", "formal-temporal", "formal-safety-mutant",
                      "formal-counterexample-temporal", "formal-counterexample-projection"}
    if len(roles) != len(formal_roles) or set(roles) != expected_roles:
        raise ValueError("formal invocation role inventory differs")
    formal_calls = sum(roles.values())
    formal_rejections = sum(roles[name] for name in expected_roles if name not in {"formal-simulation", "formal-temporal"})
    formal_verifies = roles["formal-temporal"] + roles["formal-counterexample-temporal"]
    if (formal_calls, formal_rejections, formal_verifies) != (7, 5, 3):
        raise ValueError("formal semantic contribution differs from receipt contract")
    nonverify, verify = map(int, base[0])
    base_quint = sampling + interaction + nonverify + verify + root_count
    base_external = int(external[0]) + base_quint
    return {"preflightMutants": len(mutants), "baseRejections": 71 + len(mutants),
            "formalTests": formal_count, "roots": root_count,
            "totalRejections": 71 + len(mutants) + formal_rejections * formal_count,
            "baseExternal": base_external, "baseQuint": base_quint, "baseVerify": verify,
            "external": base_external + formal_calls * formal_count,
            "quint": base_quint + formal_calls * formal_count, "verify": verify + formal_verifies * formal_count}


def self_test(source, formal_count, root_count):
    actual = accounting(source, formal_count, root_count)
    mutants = {
        "old-eleven-mutant-constant": source.replace(
            "71 + List.length preflightMutants + 5 * declaredFormalTestCount",
            "82 + 5 * declaredFormalTestCount"),
        "wrong-executed-list": source.replace(
            "for main, step, invariant in preflightMutants do",
            "for main, step, invariant in [] do"),
        "duplicate-mutant": source.replace(
            '            "PreflightArtifactModel", "mutantStep", "safety"\n',
            '            "PreflightArtifactModel", "mutantStep", "safety"\n' * 2),
        "missing-focused-census": source.replace(
            "if quintRejectedProcessCount - preflightRejectedProcessBaseline <> List.length preflightMutants then",
            "if false then"),
        "missing-full-census": source.replace(
            "if quintRejectedProcessCount <> expectedRejectedProcessCount then", "if false then"),
        "missing-formal-contribution": source.replace(
            "71 + List.length preflightMutants + 5 * declaredFormalTestCount",
            "71 + List.length preflightMutants"),
    }
    for name, changed in mutants.items():
        if changed == source:
            raise ValueError("ineffective source mutation: " + name)
        try:
            accounting(changed, formal_count, root_count)
        except ValueError:
            continue
        raise ValueError("source mutation escaped: " + name)
    if accounting(source, 0, root_count)["totalRejections"] != actual["baseRejections"]:
        raise ValueError("base shard count differs")
    if accounting(source, formal_count + 1, root_count)["totalRejections"] != actual["totalRejections"] + 5:
        raise ValueError("formal contribution does not compose")
    return len(mutants) + 3


def verify_consumers(root, plan):
    source = (root / "src/FS.GG.Coordination.Qualification.Contracts/BootstrapCi.fs").read_text()
    source = source.split("let private inspectCanonicalQuintReceipt ", 1)[1].split("let private inspectCanonicalQuintArtifact", 1)[0]
    prefix = "passed|passed|8|{totalRejections}|{external}|{quint}|{verify}|".format(**plan)
    if source.count(prefix) != 1:
        raise ValueError("current typed receipt digest uses stale accounting")
    for field, value in [("negativeControlCount", plan["totalRejections"]),
                         ("external", plan["external"]), ("quintCli", plan["quint"]),
                         ("apalacheVerify", plan["verify"])]:
        if not re.search(r'int64Property "' + field + r'" (?:root|processCounts) <> Some ' + str(value) + r'L', source):
            raise ValueError("current typed receipt predicate differs: " + field)
    for field, value, retry in [("external", plan["external"], "startupRetryCount"),
                                ("quintCli", plan["quint"], "startupRetryCount"),
                                ("apalacheVerify", plan["verify"], "verifyRetryCount")]:
        if not re.search(r'int64Property "' + field + r'" physicalProcessCounts\s*<> Some\(' + str(value) + r'L \+ ' + retry + r'\)', source):
            raise ValueError("current physical receipt predicate differs: " + field)
    fixture = (root / "tests/FS.GG.Coordination.ArchitectureTests/BootstrapCiTests.fs").read_text()
    if prefix not in fixture:
        raise ValueError("current typed fixture digest uses stale accounting")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    source = (args.root / "eng/validate-canonical-quint-protocol.fsx").read_text()
    configuration = json.loads((args.root / "eng/quint-qualification.json").read_text())
    current_source = hashlib.sha256((args.root / configuration["source"]).read_bytes()).hexdigest()
    if configuration["sourceSha256"] != current_source:
        raise ValueError("qualification configuration is not current source")
    identity = (args.root / "src/FS.GG.Coordination.Qualification.Contracts/CanonicalProtocolSourceIdentity.fs").read_text()
    if not re.search(r'let CurrentSha256 =\s*"' + current_source + '"', identity):
        raise ValueError("typed receipt identity is not current source")
    formal_count = len(configuration["formalTests"])
    root_count = len(configuration["roots"])
    for name in ["formalTests", "roots"]:
        if len({row["id"] for row in configuration[name]}) != len(configuration[name]):
            raise ValueError("duplicate configured " + name)
    result = accounting(source, formal_count, root_count)
    verify_consumers(args.root, result)
    if args.self_test:
        result["pureControlsPassed"] = self_test(source, formal_count, root_count)
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()
