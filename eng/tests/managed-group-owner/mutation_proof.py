"""Mutation proof for eng/managed_group_owner.py.

Each mutant replaces exactly one source fragment and must make its named focused
controls fail; the unmutated owner must pass the same controls. Containment mutants
run in a fresh delegated unit each (delegated-unit.sh); refusal mutants run directly.
Usage: mutation_proof.py --scratch DIR --mode refusal|all
"""
import argparse, json, os, pathlib, shutil, subprocess, sys, time, uuid

HERE = pathlib.Path(__file__).resolve().parent
OWNER = HERE.parents[1] / 'managed_group_owner.py'
SUITE = HERE / 'test_managed_group_owner.py'
UNIT = HERE / 'delegated-unit.sh'
R, C = 'RefusalControls.', 'ContainmentControls.'
HANG_KILLS = {'deadline-ignored'}
MUTANTS = [
    ('executable-digest-unchecked', "_require(sha256_fd(exe_fd) == profile['executable']['sha256'], 'executable-identity-mismatch')", 'sha256_fd(exe_fd)', [R + 'test_executable_identity_mismatch_refuses_before_launch']),
    ('probe-unchecked', "_require(available['available'], 'containment-unavailable', str(available['refusal']))", 'pass', [R + 'test_containment_unavailable_refuses_before_launch']),
    ('alarm-unchecked', "_require(signal.getitimer(signal.ITIMER_REAL) == (0.0, 0.0), 'owner-alarm-active')", 'pass', [R + 'test_active_alarm_refuses']),
    ('environment-unchecked', "_require(isinstance(env, dict) and sorted(env) == profile['environmentKeys']", "_require(True or sorted(env) == profile['environmentKeys']", [R + 'test_environment_mismatch_refuses']),
    ('cgroup-kill-skipped', "_write(os.path.join(leaf, 'cgroup.kill'), b'1'); retire['cgroupKill'] = True", "retire['cgroupKill'] = True", [C + 'test_session_escape_stays_contained_and_require_empty_refuses', C + 'test_deadline_kills_stubborn_group']),
    ('escape-census-blind', "escaped.setdefault((pid, row['start']), os.path.relpath(directory, droot)); found = True", 'pass', [C + 'test_cgroup_escape_is_refused_and_retired']),
    ('escaped-not-killed', 'if _pidfd_signal(pid, start, signal.SIGKILL) == \'sent\':', 'if False:', [C + 'test_cgroup_escape_is_refused_and_retired']),
    ('foreign-cgroup-kept', "_remove_tree(path, end); entry['removed'] = True", "entry['removed'] = True", [C + 'test_cgroup_escape_is_refused_and_retired']),
    ('launch-outside-leaf', "os.write(fd, b'0')", 'pass', [C + 'test_success_binds_birth_and_contains_leader']),
    ('no-new-privs-dropped', 'if libc.prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0:', 'if False:', [C + 'test_success_binds_birth_and_contains_leader']),
    ('leader-image-unchecked', "('not-admitted', 'unobservable')", "('unobservable',)", [C + 'test_leader_image_outside_profile_is_refused']),
    ('remove-failure-hidden', "retire['removeError'] = describe(error)", "retire['leafRemoved'] = True", [C + 'test_cleanup_failure_is_reported_not_hidden']),
    ('require-empty-ignored', "if residual and retirement['onLeaderExit'] == 'require-empty':", 'if False:', [C + 'test_session_escape_stays_contained_and_require_empty_refuses']),
    ('deadline-ignored', "            if time.monotonic() >= deadline:\n                raise TimeoutError", "            if time.monotonic() >= deadline + 3600:\n                raise TimeoutError", [C + 'test_deadline_kills_stubborn_group']),
    ('exit-code-ignored', "if code['code'] != 0:", 'if False:', [C + 'test_early_exit_failure_is_reported_and_group_clean']),
    ('output-cap-unenforced', "kept = block[:max(0, caps[name] - s['bytes'])]", 'kept = block', [C + 'test_output_cap_is_enforced']),
    ('exclusivity-unchecked', "_require(not strays and not children, 'delegated-root-not-exclusive',", "_require(True, 'delegated-root-not-exclusive',", [C + 'test_non_exclusive_root_refuses_before_launch']),
    ('pids-limit-unapplied', "_write(os.path.join(leaf, 'pids.max'), str(limits['pidsMax']).encode())", 'pass', [C + 'test_pids_limit_is_enforced']),
    ('cleanup-hook-replaces-cause', "additional.append(label + ':' + describe(error))", 'primary = error', [C + 'test_cleanup_hook_failure_does_not_replace_first_cause']),
]


def execute(owner, tests, containment, scratch, timeout):
    env = {'FSGG_MANAGED_GROUP_OWNER': str(owner), 'TMPDIR': str(scratch), 'PYTHONDONTWRITEBYTECODE': '1'}
    command = [os.path.realpath(sys.executable), '-I', '-B', str(SUITE), *tests]
    unit = None
    if containment:
        env['FSGG_MANAGED_GROUP_REQUIRE_CONTAINMENT'] = '1'
        unit = 'fsgg-mg-proof-' + uuid.uuid4().hex[:12]
        command = ['bash', str(UNIT), unit, '--owner-sets-no-new-privileges', *(f'{k}={v}' for k, v in sorted(env.items())), '--', *command]
    started = time.monotonic()
    try:
        done = subprocess.run(command, env={**os.environ, **env}, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
        code, output = done.returncode, done.stdout.decode('utf-8', 'replace')
    except subprocess.TimeoutExpired as expired:
        code, output = 'timeout', (expired.stdout or b'').decode('utf-8', 'replace')
        if unit is not None:
            try:
                subprocess.run(['sudo', '-n', 'systemctl', 'stop', unit + '.service'], timeout=30)
            except subprocess.TimeoutExpired:
                output += '\nunit stop timed out'
    return {'code': code, 'seconds': round(time.monotonic() - started, 2), 'tail': output[-1500:]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--scratch', required=True); parser.add_argument('--mode', choices=('refusal', 'all'), required=True)
    parser.add_argument('--timeout', type=float, default=90)
    args = parser.parse_args()
    scratch = pathlib.Path(args.scratch); scratch.mkdir(parents=True, exist_ok=True)
    source = OWNER.read_text()
    selected = [m for m in MUTANTS if args.mode == 'all' or all(t.startswith(R) for t in m[3])]
    results = []
    for name, old, new, tests in selected:
        if source.count(old) != 1:
            raise SystemExit(f'mutation {name}: fragment occurs {source.count(old)} times')
        containment = any(t.startswith(C) for t in tests)
        baseline = execute(OWNER, tests, containment, scratch, args.timeout)
        directory = scratch / ('mutant-' + name); shutil.rmtree(directory, ignore_errors=True); directory.mkdir()
        mutant = directory / 'managed_group_owner.py'; mutant.write_text(source.replace(old, new))
        outcome = execute(mutant, tests, containment, scratch, 30 if name in HANG_KILLS else args.timeout)
        # A mutant is killed only by a failing control (unittest exit 1). A hang counts only
        # where the mutation removes the deadline itself.
        killed = baseline['code'] == 0 and (outcome['code'] == 1 or (name in HANG_KILLS and outcome['code'] == 'timeout'))
        results.append({'mutant': name, 'tests': tests, 'baseline': baseline['code'], 'mutantResult': outcome['code'],
                        'killed': killed, 'seconds': outcome['seconds'], **({} if killed else {'baselineTail': baseline['tail'], 'mutantTail': outcome['tail']})})
        print(f"{'KILLED ' if killed else 'SURVIVED'} {name}: baseline={baseline['code']} mutant={outcome['code']} ({outcome['seconds']}s)", flush=True)
    report = {'schema': 'fsgg.managed-group-mutation-proof/1', 'mode': args.mode, 'mutants': len(results),
              'killed': sum(r['killed'] for r in results), 'results': results}
    (scratch / f'mutation-proof-{args.mode}.json').write_text(json.dumps(report, indent=2) + '\n')
    if report['killed'] != report['mutants']:
        for r in results:
            if not r['killed']:
                print(json.dumps(r, indent=2))
        raise SystemExit(f"mutation proof: {report['mutants'] - report['killed']} of {report['mutants']} mutants survived")
    print(f"mutation proof: {report['killed']}/{report['mutants']} mutants killed ({args.mode})")


if __name__ == '__main__':
    main()
