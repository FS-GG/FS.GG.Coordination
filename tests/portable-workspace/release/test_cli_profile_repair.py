#!/usr/bin/env python3
"""Run the actual fresh preparation binding shell against injected Git facts."""
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[3]
workflow = (ROOT / '.github/workflows/callable-cli-release-prepare.yml').read_text()
job = workflow.split('\n  cli_profile_repair:\n', 1)[1].split('\n  execution_pair:\n', 1)[0]
step = job.split('      - name: Bind CLI-only source and prospective version\n', 1)[1].split('      - name:', 1)[0]
shell = '\n'.join(line[10:] for line in step.split('        run: |\n', 1)[1].splitlines())
scenarios = ['valid', 'wrong-version', 'wrong-ref', 'attempt-two', 'wrong-source',
             'different-checkout', 'moved-main', 'dirty', 'existing-output', 'malformed-source']
with tempfile.TemporaryDirectory(prefix='sdk1124-prepare-binding-') as temporary:
    root = Path(temporary)
    binary = root / 'bin'
    binary.mkdir()
    git = binary / 'git'
    git.write_text('''#!/usr/bin/python3
import os,sys
args=sys.argv[1:]
if args==['rev-parse','HEAD']: print(os.environ['FIXTURE_CHECKOUT'])
elif args==['rev-parse','refs/remotes/origin/main']: print(os.environ['FIXTURE_MAIN'])
elif args==['status','--porcelain']: print(' M fake' if os.environ['FIXTURE_DIRTY']=='true' else '',end='')
elif args==['fetch','--no-tags','origin','+refs/heads/main:refs/remotes/origin/main']: pass
else: raise SystemExit('unrecognized injected Git request: '+repr(args))
''')
    git.chmod(0o700)
    for scenario in scenarios:
        output = root / scenario
        env = dict(PATH=f'{binary}:/usr/bin:/bin', CANDIDATE_SOURCE='a'*40, CANDIDATE_VERSION='0.4.0',
                   GITHUB_SHA='a'*40, GITHUB_REF='refs/heads/main', GITHUB_RUN_ATTEMPT='1',
                   CANDIDATE_OUTPUT=str(output), FIXTURE_CHECKOUT='a'*40, FIXTURE_MAIN='a'*40, FIXTURE_DIRTY='false')
        if scenario == 'wrong-version': env['CANDIDATE_VERSION'] = '0.3.0'
        elif scenario == 'wrong-ref': env['GITHUB_REF'] = 'refs/heads/unreviewed'
        elif scenario == 'attempt-two': env['GITHUB_RUN_ATTEMPT'] = '2'
        elif scenario == 'wrong-source': env['CANDIDATE_SOURCE'] = 'b'*40
        elif scenario == 'different-checkout': env['FIXTURE_CHECKOUT'] = 'b'*40
        elif scenario == 'moved-main': env['FIXTURE_MAIN'] = 'b'*40
        elif scenario == 'dirty': env['FIXTURE_DIRTY'] = 'true'
        elif scenario == 'existing-output': output.mkdir()
        elif scenario == 'malformed-source': env['CANDIDATE_SOURCE'] = '../invalid'
        result = subprocess.run(['/bin/bash', '-c', shell], cwd=root, env=env, capture_output=True, timeout=10)
        assert (result.returncode == 0) == (scenario == 'valid'), (scenario, result.stderr)
print(f'CLI_PROFILE_PREPARATION_BINDING passed={len(scenarios)} injectedGit=true nativeEffects=0')
