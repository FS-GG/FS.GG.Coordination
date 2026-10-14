#!/usr/bin/env python3
"""Build and optionally bind the exact callable CLI release readback receipt."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys


def refuse(message: str) -> None:
    print(f"CALLABLE_CLI_RELEASE_READBACK_REFUSED {message}", file=sys.stderr)
    raise SystemExit(2)


def sha(value: str, size: int) -> str:
    if not isinstance(value, str) or len(value) != size or any(character not in "0123456789abcdef" for character in value):
        refuse(f"identity must be lowercase hexadecimal with length {size}")
    return value


def feed(path: Path, name: str, package_sha256: str) -> dict[str, object]:
    if not path.is_file():
        refuse(f"verified {name} feed result is absent")
    try:
        value = json.loads(path.read_bytes())
    except (OSError, json.JSONDecodeError):
        refuse(f"verified {name} feed result is invalid")
    expected_keys = {"feed", "candidateSha256", "servedArchiveSha256", "payloadMatch"}
    if not isinstance(value, dict) or set(value) != expected_keys:
        refuse(f"verified {name} feed result shape changed")
    if value["feed"] != name or value["candidateSha256"] != package_sha256 or value["payloadMatch"] is not True:
        refuse(f"verified {name} feed result identity changed")
    sha(value["servedArchiveSha256"], 64)
    return value



# The existing readback helper owns both the pre-effect and final proof join.
PAIR_RECEIVERS = {
    'sdd-s1': ('FS-GG/FS.GG.SDD', 'd0bb4059d0fcbed5acf1f6714ee8ce53014b746f', 71),
    'fsquint-s1': ('FS-GG/FsQuint', '708587f20c0228d3e55ce8378e4a8770c18fc3ce', 124),
    'sdd-rollback': ('FS-GG/FS.GG.SDD', 'c246d2cf12eeb04d6894bab7528c7929bb87a762', 66),
    'fsquint-rollback': ('FS-GG/FsQuint', '34eeca981c136144ced73a3f98b5ce04218e89c7', 73),
}


# Separate SDK compiler input; it is never a third Execution pair member.
PAIR_SDK_CORE = {'version':'10.1.401','packageBytes':3049582,'packageSha256':'17a9cfd456ce207ebf721eef50d8e1f70aca5e70e2d265a62ca704211526c0ff','contentHash':'IGqjL9U8pQl7CaCpFP3f5slZMduE8c7ZBx7HycAQ9dc1tawyniNyw7Fh6L2cShIoQDcaDL+S8/kokhWs8WwnBQ==','dllSha256':'9abd1f889fc8e2d2a74d6ac83a31e6c3012c075a17946a7d687c0cbe33ed15be','dllRelativePath':'lib/netstandard2.1/FSharp.Core.dll'}
PAIR_SDK_SOURCE_LOCKS = {
    'eng/Qualification/packages.lock.json':'b5bfa61543a05cc3000b2cb2ff78cae75bcaff82904f713ea5ae5d63e13660dd',
    'examples/AutomataProviders/packages.lock.json':'1aa7ab8affbbc8f27c85aa29f2cc30178b6326d7c20f788ce248226282c31636',
    'examples/AutomataReplay/packages.lock.json':'05de5013ea3fc0a0c1ba33e9c530725a92c80562de351bade404540bab2cfae5',
    'examples/BoundedQueue/packages.lock.json':'150bb813c456fb7338faea0f830b5fdafb30c9c4e8da764578e2983580173845',
    'src/FsQuint.Tooling/packages.lock.json':'29875a191450e7290f08bfc95007dba3df0a1df13f3719cd7d71115ea746cbd5',
    'src/FsQuint/packages.lock.json':'b5bfa61543a05cc3000b2cb2ff78cae75bcaff82904f713ea5ae5d63e13660dd',
    'tests/FsQuint.Tests/packages.lock.json':'0a7a088441a92473e40d7354dd5b78e3806aa3d798392d5904ec2085944bdd32',
}


def pair_sdk_bundled_input(case, closure, profile, source_locks_raw, binding_raw):
    def require(ok, reason):
        if not ok: refuse('four receiver SDK Core ' + reason)
    if case != 'fsquint-s1':
        require(closure['sdkBundledInput'] is None and binding_raw is None, 'unexpected receiver profile')
        return
    spec = PAIR_SDK_CORE
    require(binding_raw is not None and 0 < len(binding_raw) <= 16384, 'proof missing/over cap')
    binding = json.loads(binding_raw); sdk = Path(profile['installation']); feed = sdk.parent/'proc6-feed'
    require(binding == closure['sdkBundledInput'] and binding['expected'] == spec and binding['sdkVersion'] == '10.0.401'
            and sdk.is_absolute() and sdk.name == 'proc6-dotnet' and profile['hostPath'] == str(sdk/'dotnet')
            and binding['sdkSourcePath'] == str(sdk/'sdk/10.0.401/FSharp/library-packs/FSharp.Core.10.1.401.nupkg')
            and binding['coldFeedPath'] == str(feed/'FSharp.Core.10.1.401.nupkg')
            and binding['sourceLockPinsSha256'] == hashlib.sha256(source_locks_raw).hexdigest()
            and binding['implicitFallbackEnabled'] is False and binding['sameVersionPublicSubstitutionAccepted'] is False,
            'source/cold-feed/original-lock binding differs')
    require(binding['assemblyPayloads'] == {'lib/netstandard2.0/FSharp.Core.dll':'26d0e3e00c769b9b6e9d8c1925885f7c16a51c605f795093386a25b909012991',spec['dllRelativePath']:spec['dllSha256']}, 'original assembly payload differs')
    pins = json.loads(source_locks_raw)
    original = {'type':'Direct','requested':'[10.1.401, )','resolved':spec['version'],'contentHash':spec['contentHash']}
    captured = [lock for lock in pins['locks'] if any(e['packageId'] == 'FSharp.Core' for e in lock['entries'])]
    require(len(captured) == 7 and {v['path']:v['sha256'] for v in captured} == PAIR_SDK_SOURCE_LOCKS, 'immutable seven-source-lock population differs')
    for lock in captured:
        rows = [e for e in lock['entries'] if e['packageId'] == 'FSharp.Core']
        require(rows == [{'framework':'net10.0','packageId':'FSharp.Core','entry':original}], 'immutable original Core lock row differs')
    cache = [v for v in closure['cachePackageIdentities'] if v['packageId'] == 'FSharp.Core']
    require(len(cache) == 1 and cache[0]['version'] == spec['version'] and cache[0]['archiveBytes'] == spec['packageBytes']
            and cache[0]['archiveSha256'] == spec['packageSha256'] and cache[0]['metadataContentHash'] == spec['contentHash']
            and cache[0]['cacheSha512ConventionAsserted'] is False, 'cold cache bytes/normalized hash differ')
    compiled = {'src/FsQuint/packages.lock.json','src/FsQuint.Tooling/packages.lock.json','tests/FsQuint.Tests/packages.lock.json'}
    locks = [v for v in closure['actualLocks'] if any(e['packageId'] == 'FSharp.Core' for e in v['entries'])]
    require(len(locks) == 3 and {v['path'] for v in locks} == compiled, 'selected compiled lock population differs')
    for lock in locks:
        require(lock['sha256'] == PAIR_SDK_SOURCE_LOCKS[lock['path']] and lock['generatedForThisReceiver'] is False
                and [e for e in lock['entries'] if e['packageId'] == 'FSharp.Core'] == [{'framework':'net10.0','packageId':'FSharp.Core','entry':original}], 'actual Core lock differs from original source')
    assets = [v for v in closure['actualAssetHashes'] if v['packageId'] == 'FSharp.Core']
    require(len(assets) == 3 and {v['path'] for v in assets} == {str(Path(p).parent/'obj/project.assets.json') for p in compiled}
            and all(v['contentHash'] == spec['contentHash'] for v in assets), 'actual asset hash join differs')
    target = sdk.parent/'proc6-state/packages/fsharp.core/10.1.401'/spec['dllRelativePath']
    references = {v[1] for v in closure['compilerInputs'] if v[0] == 'R' and Path(v[1]).name == 'FSharp.Core.dll'}
    copies = closure['runtimeCopies'].get('FSharp.Core')
    require(references == {str(target)} and isinstance(copies, list) and 0 < len(copies) <= 128
            and all(isinstance(p,str) and not Path(p).is_absolute() and '..' not in Path(p).parts and 'bin' in Path(p).parts and Path(p).name == 'FSharp.Core.dll' for p in copies), 'compiler/runtime copy correspondence absent')
    # R8 hashes each compiler/runtime file against spec.dllSha256 before producing this
    # paths-only receipt; native exact-workflow success supplies that trusted observation.


def pair_consumer_selection(admission: dict) -> dict:
    q = admission.get('consumerQualification')
    if not isinstance(q, dict) or set(q) != {'executor', 'runId', 'workflowSha256', 'cases'}:
        refuse('four receiver qualification selection absent')
    sha(q['executor'], 40); sha(q['workflowSha256'], 64)
    if type(q['runId']) is not int or q['runId'] <= 0 or set(q['cases']) != set(PAIR_RECEIVERS):
        refuse('four receiver native run or population unresolved')
    jobs, artifacts = set(), set()
    for case, row in q['cases'].items():
        if not isinstance(row, dict) or set(row) != {'jobId', 'artifactId', 'archiveSha256', 'observationSha256', 'sourceClosureSha256'}:
            refuse('four receiver member selection changed')
        for key, seen in [('jobId', jobs), ('artifactId', artifacts)]:
            if type(row[key]) is not int or row[key] <= 0 or row[key] in seen:
                refuse('four receiver native identities unresolved or duplicated')
            seen.add(row[key])
        for key in ('archiveSha256', 'observationSha256', 'sourceClosureSha256'): sha(row[key], 64)
    return q


def pair_consumers(admission: dict, root: Path) -> dict:
    """Join authenticated native downloads; never execute or extract their contents."""
    import re
    import stat
    import zipfile
    import xml.etree.ElementTree as ET
    q = pair_consumer_selection(admission)
    def require(ok, reason):
        if not ok: refuse('four receiver ' + reason)
    def raw(path, cap=1048576):
        require(not path.is_symlink() and path.is_file() and 0 < path.stat().st_size <= cap, 'file absent/unsafe/over cap')
        return path.read_bytes()
    def document(path): return json.loads(raw(path))
    run = document(root/'run.json'); jobs = document(root/'jobs.json')
    require(run['id'] == q['runId'] and run['workflow_id'] == 361024581
            and run['repository']['id'] == 1346720714 and run['head_repository']['id'] == 1346720714
            and run['head_sha'] == q['executor'] and run['head_branch'] == 'main'
            and run['path'] == '.github/workflows/callable-cli-release-prepare.yml'
            and run['event'] == 'workflow_dispatch' and run['run_attempt'] == 1
            and run['status'] == 'completed' and run['conclusion'] == 'success', 'original executor run differs')
    require(jobs['total_count'] == len(jobs['jobs']) and 4 <= len(jobs['jobs']) <= 100, 'incomplete native job page')
    require(len({j['id'] for j in jobs['jobs']}) == len(jobs['jobs']), 'duplicate native job')
    results = {}; runner_ids = set(); transfer_identity = None
    for case, selected in q['cases'].items():
        repository, receiver, count = PAIR_RECEIVERS[case]
        matching = [j for j in jobs['jobs'] if j['name'] == 'Installed receiver ' + case]
        require(len(matching) == 1, 'named native job absent/duplicated')
        job = matching[0]
        require(job['id'] == selected['jobId'] and job['run_id'] == q['runId'] and job['head_sha'] == q['executor']
                and job['run_attempt'] == 1 and job['status'] == 'completed' and job['conclusion'] == 'success', 'native job differs/failed')
        require(type(job['runner_id']) is int and job['runner_id'] > 0 and job['runner_id'] not in runner_ids
                and job['runner_group_name'] == 'GitHub Actions' and 'ubuntu-24.04' in job['labels'], 'independent hosted runner observation missing')
        runner_ids.add(job['runner_id'])
        setup = [s for s in job['steps'] if s['name'] == 'Set up job']
        require(len(setup) == 1 and setup[0]['conclusion'] == 'success', 'native setup did not succeed')
        # Retain actual native setup text. This observes provider image identity, not kernel/network denial.
        log = raw(root/(case+'.job.log'), 8388608).decode('utf-8', errors='strict')
        groups = re.findall(r'##\[group\]Runner Image\s*\n(.*?)##\[endgroup\]', log, re.S)
        require(len(groups) == 1, 'native setup image group absent/duplicated')
        image = re.search(r'(?m)^.*Image: (ubuntu-24\.04)\s*$', groups[0])
        image_version = re.search(r'(?m)^.*Version: ([0-9]{8}\.[0-9]+(?:\.[0-9]+)?)\s*$', groups[0])
        require(image is not None and image_version is not None, 'native image identity absent')
        meta = document(root/(case+'.artifact.json'))
        require(meta['id'] == selected['artifactId'] and meta['name'] == f'execution-pair-consumer-{case}-{q["runId"]}-1'
                and meta['expired'] is False and meta['workflow_run']['id'] == q['runId']
                and meta['workflow_run']['head_sha'] == q['executor']
                and meta['workflow_run']['repository_id'] == 1346720714
                and meta['workflow_run']['head_repository_id'] == 1346720714
                and meta['digest'] == 'sha256:'+selected['archiveSha256'], 'native artifact identity differs')
        archive = raw(root/(case+'.zip'), 33554432)
        require(len(archive) == meta['size_in_bytes'] and hashlib.sha256(archive).hexdigest() == selected['archiveSha256'], 'artifact bytes differ')
        import io
        with zipfile.ZipFile(io.BytesIO(archive)) as z:
            infos = z.infolist(); names = [i.filename for i in infos]
            require(0 < len(names) <= 512 and len(names) == len(set(names)) and sum(i.file_size for i in infos) <= 33554432, 'ZIP population/expanded cap')
            for i in infos:
                parts = i.filename.rstrip('/').split('/')
                require(not i.filename.startswith('/') and '\\' not in i.filename and not any(p in ('', '.', '..') for p in parts)
                        and not i.flag_bits & 1 and stat.S_IFMT(i.external_attr >> 16) in (0, stat.S_IFREG, stat.S_IFDIR), 'unsafe ZIP member')
            def member(name):
                info = z.getinfo(name)
                require(not info.is_dir() and 0 < info.file_size <= 1048576, 'proof member cap')
                return z.read(info)
            source_inputs = json.loads(member('proc6-state/source-inputs.json'))
            budget = json.loads(member('proc6-state/budget.json'))
            source_locks_raw = member('proc6-state/proof/source-lock-pins.json')
            source_locks = json.loads(source_locks_raw)
            transfer = json.loads(member('proc6-transfer-native.json'))
            require(source_inputs['receiverRepository'] == repository and source_inputs['receiverCommit'] == receiver
                    and source_inputs['producerSourceTransferred'] is False and source_inputs['networkReachable'] is True
                    and source_inputs['hardContainment'] is False and source_locks['receiverSource'] == receiver, 'receiver source inputs differ')
            require(budget['workSeconds'] == 600 and budget['cleanupSeconds'] == 30 and budget['reportSeconds'] == 30, 'original budget profile differs')
            require(type(transfer['id']) is int and transfer['id'] > 0 and transfer['expired'] is False
                    and transfer['name'] == 'execution-pair-installed-'+admission['source']+'-'+str(q['runId'])
                    and transfer['workflow_run']['id'] == q['runId'] and transfer['workflow_run']['head_sha'] == q['executor']
                    and transfer['workflow_run']['repository_id'] == 1346720714 and transfer['workflow_run']['head_repository_id'] == 1346720714,
                    'current-run acquisition artifact join differs')
            identity = (transfer['id'], transfer['digest'])
            if transfer_identity is None: transfer_identity = identity
            require(identity == transfer_identity, 'receivers used different acquisition artifacts')
            observation_raw = member('proc6-state/proof/consumer-observation.json')
            closure_raw = member('proc6-state/proof/source-closure.json')
            require(hashlib.sha256(observation_raw).hexdigest() == selected['observationSha256']
                    and hashlib.sha256(closure_raw).hexdigest() == selected['sourceClosureSha256'], 'selected proof bytes differ')
            o = json.loads(observation_raw); c = json.loads(closure_raw)
            require(o['schema'] == 'fsgg.coordination.execution-pair-consumer-observation/1'
                    and o['case'] == case and o['source'] == admission['source'] and o['executor'] == q['executor']
                    and o['runId'] == str(q['runId']) and o['attempt'] == 1 and o['receiver'] == receiver
                    and o['receiverRepository'] == repository and o['candidateArchiveSha256'] == admission['archiveSha256']
                    and o['sourceClosureSha256'] == selected['sourceClosureSha256'], 'observation source/run/candidate join differs')
            for key in ('nativeFixtureInvoked', 'selectedFixturesPassed', 'fixtureCleanupObserved', 'networkReachable'):
                require(o[key] is True, 'required observation missing: '+key)
            for key in ('consumerAdoptionAccepted', 'publicationAuthorized', 'hardContainment', 'outerCustodyQualified', 'pipelineDeadlineQualified', 'currentCrossRepoArtifactAccessEstablished'):
                require(o[key] is False, 'unsupported stronger claim: '+key)
            require(type(o['elapsedSeconds']) in (int, float) and 0 <= o['elapsedSeconds'] <= o['originalWorkSeconds'] <= 600
                    and o['originalWorkSeconds'] == budget['workSeconds'] and o['ordinaryCommandTimeoutSeconds'] == 90, 'original deadline evidence differs')
            require(c['physicalSourceExclusionEstablished'] is True and c['networkReachable'] is True and c['hardContainment'] is False
                    and c['scope'] == 'known fresh hosted workspace/temp and evaluated compiler inputs'
                    and c['nativeFixtureInvoked'] is False and c['consumerAdoptionAccepted'] is False, 'source exclusion scope differs')
            require(c['sdkVersion'] == o['sdkVersion'] == '10.0.401'
                    and c['runtimeFrameworkVersion'] == o['runtimeFrameworkVersion'] == '10.0.12', 'runtime profile differs')
            profile_raw = member('proc6-state/proof/dotnet-profile.json')
            require(hashlib.sha256(profile_raw).hexdigest() == c['dotnetProfileSha256'] == o['dotnetProfileSha256'], 'actual dotnet profile join differs')
            require(c['compilerInputs'] and c['compilerEvaluations'] and c['runtimeCopies'] and c['cachePackageIdentities']
                    and c['actualLocks'] and c['actualAssetHashes'], 'compiled/cache/lock closure absent')
            require(c['runtimeConfigurations'] and all(r['runtimeVersion'] == '10.0.12' and r['rollForward'] == 'Disable' for r in c['runtimeConfigurations']), 'built runtime configs differ')
            require(all(type(lock['schemaVersion']) is int and lock['schemaVersion'] in (1, 2) for lock in c['actualLocks']), 'typed lock schema must be 1 or 2')
            pair_sdk_bundled_input(case, c, json.loads(profile_raw), source_locks_raw,
                                   member('proc6-state/proof/sdk-core-input.json') if case == 'fsquint-s1' else None)
            if case.endswith('s1'):
                package_hashes = admission['packages']; version = '0.3.0'
            elif case == 'sdd-rollback':
                package_hashes = {'FS.GG.Execution.Contracts':'62b1f85b8622d6719f086d73f4dbf29bd251dbb083a9b9588535d6b86531665d',
                                  'FS.GG.Execution.Processes':'20d09f3bc71c78c46809931a8d1f7b4009d381fb0c4f203db2d7c846266e4f41'}; version = '0.2.0'
            else:
                package_hashes = {'FsQuint':'e170d70a683321818944d86bb9c9e087323e01391738b3b4a621892ef91a2a8a',
                                  'FsQuint.Tooling':'d2a58ae6431281e02fe19a26670225a5bd77c50450f473e4a8031c6fa47ebc79'}; version = '0.1.0'
            require(c['packageVersions'] == source_inputs['expectedPackages'] and set(c['packageVersions']) == set(package_hashes), 'coherent package population differs')
            for package, digest in package_hashes.items():
                expected = c['packageVersions'][package]
                require(expected['version'] == version and expected['packageSha256'] == digest
                        and type(expected['packageBytes']) is int and expected['packageBytes'] > 0
                        and c['runtimeCopies'].get(package), 'selected package/runtime correspondence differs')
                sha(expected['dllSha256'], 64)
                cache = [r for r in c['cachePackageIdentities'] if r['packageId'] == package]
                require(len(cache) == 1 and cache[0]['version'] == version and cache[0]['archiveSha256'] == digest
                        and cache[0]['archiveBytes'] == expected['packageBytes'] and cache[0]['cacheSha512ConventionAsserted'] is False, 'actual cached archive differs')
                normalized = cache[0]['metadataContentHash']
                locks = [r['entry'] for lock in c['actualLocks'] for r in lock['entries'] if r['packageId'] == package]
                assets = [r for r in c['actualAssetHashes'] if r['packageId'] == package]
                require(isinstance(normalized, str) and normalized and locks and assets
                        and all(r['resolved'] == version and r['contentHash'] == normalized for r in locks)
                        and all(r['contentHash'] == normalized for r in assets), 'actual normalized cache/lock/assets join differs')
            expected_archive = admission['archiveSha256'] if case.endswith('s1') else '6aedfb7e4eda796fdea3fc0326003a6f90f859352d7394d311f4c5d867687819' if case == 'sdd-rollback' else None
            require(o['selectedPackageArchiveSha256'] == expected_archive, 'rollback/candidate archive distinction lost')
            if case == 'fsquint-rollback':
                signature = json.loads(member('proc6-state/proof/public-signature-verification.json'))
                require(o['originalArtifactReacquired'] is False and o['historicalOriginalArchiveSha256'] == '7078f08faed9d8f1e6a7a662fd33c04861a106990db61b184d3e573c413f8c1f'
                        and o['historicalReceiptSha256'] == 'fc0c5c4f774b7210dbfda88fcbae97759994f36424b9c889c693dcf8d7ac4f3c'
                        and o['freshPublicSignatureVerification'] == signature and signature['currentVerificationObserved'] is True
                        and signature['historicalVerificationSubstituted'] is False, 'fresh signed rollback proof absent')
                require(len(signature['packages']) == 2 and {r['packageId'] for r in signature['packages']} == set(package_hashes), 'fresh signature package population differs')
                for row in signature['packages']:
                    signature_log = member('proc6-state/logs/'+row['packageId']+'-signature.log')
                    require(type(row['exitCode']) is int and row['exitCode'] == 0 and row['verifyAll'] is True
                            and row['packageSha256'] == package_hashes[row['packageId']]
                            and 0 < len(signature_log) <= 65536 and hashlib.sha256(signature_log).hexdigest() == row['logSha256'], 'fresh signature verifier/log join differs')
            fixtures = []
            if case.startswith('sdd-'):
                for index, n in enumerate((18 if case == 'sdd-s1' else 13, 8, 17, 27, 1)):
                    t = ET.fromstring(member(f'proc6-state/proof/results/{index}.trx')); ns = {'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
                    summary = t.find('t:ResultSummary', ns); counters = summary.find('t:Counters', ns)
                    rows = t.findall('t:Results/t:UnitTestResult', ns)
                    require(summary.get('outcome') == 'Completed' and all(int(counters.get(k, '-1')) == n for k in ('total', 'executed', 'passed'))
                            and all(int(counters.get(k, '-1')) == 0 for k in ('failed', 'error', 'timeout', 'aborted', 'notExecuted'))
                            and len(rows) == n and len({r.get('testId') for r in rows}) == n and all(r.get('outcome') == 'Passed' for r in rows), 'TRX results differ')
                    fixtures += [r.get('testName') for r in rows]
            else:
                text = member('proc6-state/logs/fixture-0.log').decode('utf-8', errors='strict')
                fixtures = [line[5:] for line in text.splitlines() if line.startswith('PASS ')]
                require(re.findall(r'^([0-9]+) checks passed$', text, re.M) == [str(count)] and not any(line.startswith('FAIL ') for line in text.splitlines()), 'whole FsQuint results differ')
            require(type(o['fixtureCount']) is int and o['fixtureCount'] == count == len(fixtures)
                    and fixtures == o['fixtureNames'] and len(set(fixtures)) == count, 'fixture population/names differ')
        results[case] = {'jobId':job['id'], 'artifactId':meta['id'], 'archiveSha256':selected['archiveSha256'],
                         'observationSha256':selected['observationSha256'], 'sourceClosureSha256':selected['sourceClosureSha256'],
                         'runnerId':job['runner_id'], 'runnerName':job['runner_name'], 'image':image.group(1), 'imageVersion':image_version.group(1),
                         'nativeJobLogSha256':hashlib.sha256(raw(root/(case+'.job.log'), 8388608)).hexdigest(), 'observation':o}
    return {'executor':q['executor'], 'runId':q['runId'], 'runAttempt':1, 'cases':results, 'acquisitionArtifactId':transfer_identity[0], 'acquisitionArtifactDigest':transfer_identity[1],
            'sourceExclusionScope':'known fresh hosted workspace/temp and evaluated compiler inputs',
            'networkReachable':True, 'hardContainment':False, 'consumerAdoptionAccepted':False, 'publicationAuthorized':False}


def pair_readback(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser()
    for name in ("publisher", "source", "tree", "merge", "manifest-sha256"):
        parser.add_argument("--" + name, required=True)
    for name in ("manifest", "feed-root", "installed", "output", "admission", "consumer-root"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--original-root", type=Path)
    parser.add_argument("--recovery-operation")
    args = parser.parse_args(arguments)
    for identity in (args.publisher, args.source, args.tree, args.merge): sha(identity, 40)
    sha(args.manifest_sha256, 64)
    raw = args.manifest.read_bytes()
    if hashlib.sha256(raw).hexdigest() != args.manifest_sha256: refuse("pair manifest digest changed")
    manifest = json.loads(raw)
    ids = {"FS.GG.Execution.Contracts", "FS.GG.Execution.Processes"}
    if (manifest["schema"] != "fsgg.coordination.execution-pair-release-preparation/1" or manifest["profile"] != "execution-pair"
        or manifest["version"] != "0.3.0" or manifest["tag"] != "execution/v0.3.0"
        or manifest["sourceCommit"] != args.source or manifest["sourceTree"] != args.tree
        or manifest["publicationAuthorized"] is not False or manifest["tagAuthorized"] is not False
        or len(manifest["packages"]) != 2 or {row["packageId"] for row in manifest["packages"]} != ids):
        refuse("pair manifest source/member identity changed")
    installed = json.loads(args.installed.read_bytes())
    if (installed["schema"] != "fsgg.coordination.execution-pair-installed-load/1" or installed["mode"] != "public"
        or installed["publicOnly"] is not True or installed["producerProjectReferenced"] is not False
        or installed["packageInputsOnly"] is not True or installed["physicalSourceExclusionEstablished"] is not False
        or installed["privateCachesUnavailable"] is not True or installed["effectiveProjectIsolationGuard"] is not True
        or installed["effectiveNuGetFoldersVerified"] is not True or installed["actualCompilerReferencesVerified"] is not True
        or installed["nativeFixtureInvoked"] is not False
        or installed["consumerAdoptionAccepted"] is not False or len(installed["packages"]) != 2
        or {row["packageId"] for row in installed["packages"]} != ids): refuse("pair public installed-load proof missing")
    admission = json.loads(args.admission.read_bytes())
    if (admission["source"] != args.source or admission["tree"] != args.tree or admission["manifestSha256"] != args.manifest_sha256
        or admission["protectedMerge"] != args.merge or admission["version"] != manifest["version"] or admission["tag"] != manifest["tag"]
        or admission["packages"] != {row["packageId"]: row["sha256"] for row in manifest["packages"]}):
        refuse("pair consumer admission differs from final manifest")
    original = None
    if 'recovery' in admission or args.original_root is not None or args.recovery_operation is not None:
        if args.original_root is None or args.recovery_operation != admission['recovery']['operation']:
            refuse('pair recovery original binding missing or changed')
        import runpy
        validators = runpy.run_path(str(Path(__file__).with_name('test-callable-cli-installed-harness.py')))
        root = args.original_root
        def bounded(name, cap=1048576):
            path = root / name
            if path.is_symlink() or not path.is_file() or not 0 < path.stat().st_size <= cap:
                refuse('pair original evidence unsafe, missing or oversized')
            return path.read_bytes()
        original = validators['pair_original_failure'](admission,
            *[json.loads(bounded(name)) for name in ('original-run.json','original-jobs.json','original-artifact.json')],
            bounded('original-observation.zip',192*1024**2))
        verified = validators['pair_verified_attestation'](admission,json.loads(bounded('original-attestation-verified.json')))
        original.update({'retainedAttestation':verified,'recoveryOperation':args.recovery_operation,
                         'attestationCreationRepeated':False})
    consumers = pair_consumers(admission, args.consumer_root)
    members = []
    for row in sorted(manifest["packages"], key=lambda row: row["packageId"]):
        sha(row["sha256"], 64); sha(row["assemblySha256"], 64)
        actual = [item for item in installed["packages"] if item["packageId"] == row["packageId"]]
        if actual != [{"packageId": row["packageId"], "version": "0.3.0", "runtimeSha256": row["assemblySha256"]}]: refuse("pair installed runtime join changed")
        github = feed(args.feed_root / (row["packageId"] + ".github.json"), "github-packages", row["sha256"])
        if github["servedArchiveSha256"] != row["sha256"]: refuse("pair GitHub raw archive identity changed")
        members.append({"packageId": row["packageId"], "candidateSha256": row["sha256"], "assemblySha256": row["assemblySha256"],
                        "githubPackages": github,
                        "nugetOrg": feed(args.feed_root / (row["packageId"] + ".nuget.json"), "nuget-org", row["sha256"])})
    value = {"schema": "fsgg.coordination.execution-pair-release-readback/1", "profile": "execution-pair", "publisherSource": args.publisher,
             "source": args.source, "sourceTree": args.tree, "protectedMerge": args.merge, "version": "0.3.0", "tag": "execution/v0.3.0",
             "manifestSha256": args.manifest_sha256, "packages": members, "installedLoad": installed, "consumerQualification": consumers,
             "consumerAdoptionAccepted": False}
    if original is not None: value['originalPublication'] = original
    value['actualEffects'] = {'githubPackages':'verified','nugetOrg':'verified','publicInstalledLoad':'verified','componentFinalization':'pending'}
    value['unresolvedState'] = ['component-finalization-native-readback']
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes((json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode())
    return 0

def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] == "--execution-pair":
        return pair_readback(sys.argv[2:])
    parser = argparse.ArgumentParser()
    parser.add_argument("--cli-only", action="store_true")
    parser.add_argument("--publisher", help="Reviewed publisher B for CLI-only source A")
    parser.add_argument("--source", required=True)
    parser.add_argument("--tree", required=True)
    parser.add_argument("--merge", required=True)
    parser.add_argument("--package-id", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--package-sha256", required=True)
    parser.add_argument("--bundle-sha256")
    parser.add_argument("--image-sha256")
    parser.add_argument("--manifest-sha256")
    parser.add_argument("--github-feed", required=True, type=Path)
    parser.add_argument("--nuget-feed", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--existing", type=Path)
    parser.add_argument("--original-publication", type=Path, help="Verified original failed attempt retained by readback recovery")
    args = parser.parse_args()

    sha(args.source, 40)
    sha(args.tree, 40)
    sha(args.merge, 40)
    sha(args.package_sha256, 64)
    if args.cli_only:
        if args.publisher is None or any((args.bundle_sha256, args.image_sha256, args.manifest_sha256)):
            refuse("CLI-only requires publisher identity and excludes portable assets")
        sha(args.publisher, 40)
    else:
        if args.publisher is not None or any(value is None for value in (args.bundle_sha256, args.image_sha256, args.manifest_sha256)):
            refuse("portable readback requires every original asset")
        for value in (args.bundle_sha256, args.image_sha256, args.manifest_sha256):
            sha(value, 64)
    if args.package_id != "FS.GG.Coordination.Cli" or args.version != ("0.3.0" if args.cli_only else "0.2.0"):
        refuse("package identity changed")
    value = {
        "schema": "fsgg.coordination.callable-cli-release-readback/2",
        "source": args.source,
        "sourceTree": args.tree,
        "protectedMerge": args.merge,
        "packageId": args.package_id,
        "version": args.version,
        "packageSha256": args.package_sha256,
        "portableAssets": {
            "bundleSha256": args.bundle_sha256,
            "imageArchiveSha256": args.image_sha256,
            "manifestSha256": args.manifest_sha256,
        },
        "feeds": {
            "githubPackages": feed(args.github_feed, "github-packages", args.package_sha256),
            "nugetOrg": feed(args.nuget_feed, "nuget-org", args.package_sha256),
        },
    }
    if args.cli_only:
        value["schema"] = "fsgg.coordination.callable-cli-release-readback/3"
        value["publisherSource"] = args.publisher
        del value["portableAssets"]
    if args.original_publication is not None:
        if not args.cli_only:
            refuse("original publication applies only to CLI-only recovery")
        try:
            original = json.loads(args.original_publication.read_bytes())
            keys = {"publisherSource", "runId", "runAttempt", "jobId", "observationArtifactId", "observationArchiveSha256", "conclusion", "publicReadback"}
            if not isinstance(original, dict) or set(original) != keys:
                refuse("original publication shape changed")
            sha(original["publisherSource"], 40)
            sha(original["observationArchiveSha256"], 64)
            if any(type(original[key]) is not int or original[key] <= 0 for key in ("runId", "jobId", "observationArtifactId")):
                refuse("original publication native identity changed")
            if type(original["runAttempt"]) is not int or original["runAttempt"] != 1 or original["conclusion"] != "failure" or original["publicReadback"] != "unresolved-after-404":
                refuse("original publication outcome changed")
        except (OSError, ValueError, TypeError, KeyError):
            refuse("original publication evidence unavailable")
        value["originalPublication"] = original
    expected = (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()
    if args.existing is not None:
        if not args.existing.is_file():
            refuse("existing release readback is absent")
        if args.existing.read_bytes() != expected:
            refuse("existing release readback differs from the complete expected receipt")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(expected)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
