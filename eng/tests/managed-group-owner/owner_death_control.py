"""Owner-death control: the delegated unit, not the owner, retires members when the owner dies.

``outside SCRATCH`` (host side) starts ``inside`` in a fresh delegated unit. ``inside`` starts
a SIGTERM-ignoring member and SIGKILLs its own owner process once the member is recorded.
The control passes only if the unit ends and the recorded member is gone afterwards.
"""
import hashlib, importlib.util, json, os, pathlib, signal, subprocess, sys, time, uuid

HERE = pathlib.Path(__file__).resolve().parent
MEMBER = r'''
import os, signal, sys, time
signal.signal(signal.SIGTERM, signal.SIG_IGN)
open(sys.argv[1] + '.tmp', 'w').write(f"{os.getpid()} {open('/proc/self/stat').read().rsplit(')', 1)[1].split()[19]}")
os.rename(sys.argv[1] + '.tmp', sys.argv[1])
time.sleep(300)
'''


def inside(scratch):
    spec = importlib.util.spec_from_file_location('managed_group_owner', HERE.parents[1] / 'managed_group_owner.py')
    mg = importlib.util.module_from_spec(spec); spec.loader.exec_module(mg)
    root = os.path.realpath(scratch); python = os.path.realpath(sys.executable)
    digest = hashlib.sha256(pathlib.Path(python).read_bytes()).hexdigest()
    pidfile = os.path.join(root, 'member')
    program = os.path.join(root, 'member.py'); pathlib.Path(program).write_text(MEMBER)
    profile = {'schema': mg.PROFILE_SCHEMA, 'name': 'owner-death', 'root': root, 'executable': {'path': python, 'sha256': digest},
               'leaderImages': [digest], 'argv': [python, '-I', '-S', program, pidfile], 'cwd': root, 'environmentKeys': ['PATH'],
               'containment': 'cgroup2-delegated', 'limits': {'pidsMax': None, 'memoryMaxBytes': None, 'stdoutBytes': 4096, 'stderrBytes': 4096},
               'retirement': {'onLeaderExit': 'require-empty', 'leaderExitSettleSeconds': 0, 'termGraceSeconds': 1, 'cleanupSeconds': 5}}

    def hook(event, context):
        if event == 'poll' and os.path.exists(pidfile):
            os.kill(os.getpid(), signal.SIGKILL)  # the owner dies without any retirement
    mg.run(profile, env={'PATH': '/usr/bin:/bin'}, deadline=time.monotonic() + 60, out_dir=os.path.join(root, 'out'), control=hook)
    raise SystemExit('owner survived its own SIGKILL')


def outside(scratch):
    os.makedirs(scratch, exist_ok=True)
    unit = 'fsgg-mg-owner-death-' + uuid.uuid4().hex[:12]
    done = subprocess.run(['bash', str(HERE / 'delegated-unit.sh'), unit, 'PYTHONDONTWRITEBYTECODE=1', '--',
                           os.path.realpath(sys.executable), '-I', '-B', __file__, 'inside', scratch], timeout=120)
    pid, start = (int(v) for v in pathlib.Path(scratch, 'member').read_text().split())
    end = time.monotonic() + 10
    while time.monotonic() < end:
        try:
            text = pathlib.Path(f'/proc/{pid}/stat').read_text()
            same = int(text.rsplit(')', 1)[1].split()[19]) == start and text.rsplit(')', 1)[1].split()[0] != 'Z'
        except FileNotFoundError:
            same = False
        if not same:
            break
        time.sleep(0.05)
    result = {'unitExit': done.returncode, 'member': pid, 'memberRetiredByUnit': not same}
    print('owner-death control: ' + json.dumps(result))
    if done.returncode == 0 or same:
        raise SystemExit('owner-death control failed')


if __name__ == '__main__':
    {'inside': inside, 'outside': outside}[sys.argv[1]](sys.argv[2])
