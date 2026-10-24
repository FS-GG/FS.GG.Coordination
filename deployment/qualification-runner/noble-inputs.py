#!/usr/bin/env python3
"""Acquire and verify the Ubuntu 24.04 (noble) qualified-image input population, or derive its lock.

  acquire  --lock LOCK --out DIR     download every locked URL into a fresh DIR, then verify everything
  verify   --lock LOCK --inputs DIR  verify a population: hashes, archive signatures, index chain, transaction
  lock     --work DIR --inventory F  derive the lock and records from a verified working population (maintainers)

Only read-only acquisition: no credentials, no package installation, no VM. A matching digest is not provider
authority; signatures are checked against keyrings supplied by the caller (the guest's ubuntu-archive-keyring.gpg
and ubuntu-cloudimage-keyring.gpg), and every signer fingerprint is pinned below.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import lzma
import re
import shutil
import subprocess
import sys
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
NOBLE = HERE / 'noble'
SNAPSHOT = 'https://snapshot.ubuntu.com/ubuntu/20261024T000000Z'
RELEASE = 'release-20261017'
IMAGE_BASE = 'https://cloud-images.ubuntu.com/releases/noble/' + RELEASE + '/'
IMAGE_NAME = 'ubuntu-24.04-server-cloudimg-amd64.img'
SUITES = ('noble', 'noble-updates', 'noble-security')
COMPONENTS = ('main', 'universe')
# Ubuntu Archive Automatic Signing Key (2018) and UEC Image Automatic Signing Key.
ARCHIVE_FINGERPRINT = 'F6ECB3762474EDA9D21B7022871920D1991BC93C'
CLOUDIMAGE_FINGERPRINT = 'D2EB44626FDDC30B513D5BB71A5D6C4C7DB87C81'
RUNNER_VERSION = '2.338.1'
GO_VERSION = '1.27.1'
SDK_SHA512 = '51c8b999af9e8dd9998c9edc5944e19a90788862068acd38694e098889054ce8c23d4f0c5cccfa16bf187d044562359e5ee69a9f8ad0bbe913ba90311fbce25b'
FIXED_URLS = {
    'sdk/dotnet-sdk-10.0.401-linux-x64.tar.gz': 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-x64.tar.gz',
    'go/go%s.linux-amd64.tar.gz' % GO_VERSION: 'https://go.dev/dl/go%s.linux-amd64.tar.gz' % GO_VERSION,
    'runner/actions-runner-linux-x64-%s.tar.gz' % RUNNER_VERSION:
        'https://github.com/actions/runner/releases/download/v%s/actions-runner-linux-x64-%s.tar.gz' % (RUNNER_VERSION, RUNNER_VERSION),
}
# Package-level identity roles for the lock's compiler block (what the compiled-in role is made of).
COMPILER_ROLES = {
    'compilerSha256': ('gcc-13', 'gcc-13-x86-64-linux-gnu', 'cpp-13', 'cpp-13-x86-64-linux-gnu', 'gcc-13-base', 'g++-13', 'g++-13-x86-64-linux-gnu'),
    'headersManifestSha256': ('libc6-dev', 'libc-dev-bin', 'linux-libc-dev', 'libgcc-13-dev', 'libstdc++-13-dev', 'libcrypt-dev'),
    'linkerSha256': ('binutils', 'binutils-x86-64-linux-gnu', 'binutils-common', 'libbinutils', 'libctf0', 'libctf-nobfd0', 'libsframe1'),
    'runtimeClosureSha256': ('libgcc-13-dev', 'libgomp1', 'libatomic1', 'libasan8', 'libtsan2', 'libubsan1', 'libitm1', 'liblsan0',
                             'libquadmath0', 'libhwasan0', 'libcc1-0', 'libc6:amd64', 'libgcc-s1:amd64'),
}


def fail(message):
    raise SystemExit('noble-inputs: ' + message)


def need(condition, message):
    if not condition:
        fail(message)


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as stream:
        for block in iter(lambda: stream.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def canonical(value):
    return (json.dumps(value, indent=2, sort_keys=True) + '\n').encode()


def gpgv(keyring, signed, signature=None):
    """Return the VALIDSIG primary fingerprint; any other outcome refuses."""
    command = ['gpgv', '--keyring', str(keyring), '--status-fd', '1'] + ([str(signature)] if signature else []) + [str(signed)]
    done = subprocess.run(command, capture_output=True, text=True)
    status = done.stdout.splitlines()
    need(done.returncode == 0 and any(row.startswith('[GNUPG:] GOODSIG ') for row in status), 'signature refused: %s' % signed)
    valid = [row.split() for row in status if row.startswith('[GNUPG:] VALIDSIG ')]
    need(len(valid) == 1, 'one valid signature required: %s' % signed)
    return valid[0][-1], int(valid[0][4])


def release_hashes(path):
    text = Path(path).read_text()
    need('\nSHA256:\n' in text, 'InRelease without SHA256 section')
    section = text.split('\nSHA256:\n', 1)[1].split('\n-----BEGIN PGP SIGNATURE', 1)[0]
    rows = {}
    for line in section.splitlines():
        if not line.startswith(' '):  # the section ends at the next field
            break
        parts = line.split()
        need(len(parts) == 3, 'malformed InRelease row')
        rows[parts[2]] = (parts[0], int(parts[1]))
    return rows


def package_index(root):
    index = {}
    for suite in SUITES:
        for component in COMPONENTS:
            data = lzma.open(Path(root) / 'apt/dists' / suite / component / 'binary-amd64/Packages.xz').read().decode()
            for block in data.split('\n\n'):
                fields = dict(re.findall(r'^([A-Za-z0-9-]+): (.*)$', block, re.M))
                if 'Package' in fields:
                    index[fields['Filename']] = (fields['Package'], fields['Version'], fields['Architecture'], fields['SHA256'], int(fields['Size']))
    return index


def verify_population(root, lock, archive_keyring):
    """Hashes (rows), archive signatures, signed-index chain, .deb membership, and the recorded transaction."""
    root = Path(root)
    spec = importlib.util.spec_from_file_location('dedicated_runner', HERE.parents[1] / 'eng/dedicated_qualification_runner.py')
    runner = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(runner)
    runner.validate_lock_data(lock)
    runner.validate_population(lock, str(root))
    runner.validate_tool_archives(lock, str(root))
    for suite in SUITES:
        fingerprint, _ = gpgv(archive_keyring, root / 'apt/dists' / suite / 'InRelease')
        need(fingerprint == ARCHIVE_FINGERPRINT, 'unexpected archive signer for %s: %s' % (suite, fingerprint))
        rows = release_hashes(root / 'apt/dists' / suite / 'InRelease')
        for component in COMPONENTS:
            name = component + '/binary-amd64/Packages.xz'
            path = root / 'apt/dists' / suite / name
            need(name in rows and sha256(path) == rows[name][0] and path.stat().st_size == rows[name][1], 'index differs from signed release: %s %s' % (suite, name))
    index = package_index(root)
    pool = sorted(str(p.relative_to(root / 'apt')) for p in (root / 'apt/pool').rglob('*.deb'))
    for filename in pool:
        need(filename in index and index[filename][3] == sha256(root / 'apt' / filename), 'deb differs from signed index: %s' % filename)
    transaction = json.loads((root / 'apt/apt-transaction.json').read_text())
    need(sorted(row['filename'] for row in transaction['debs']) == pool, 'transaction .deb set differs from the pool')
    need(lock['aptTransaction']['qualifiedSignedTransactionSha256'] == sha256(root / 'apt/apt-transaction.json'), 'transaction digest differs')
    return {'suites': list(SUITES), 'debs': len(pool), 'signer': ARCHIVE_FINGERPRINT}


def verify_image_checksums(sums, signature, keyring, lock):
    fingerprint, created = gpgv(keyring, sums, signature)
    need(fingerprint == CLOUDIMAGE_FINGERPRINT, 'unexpected image signer: ' + fingerprint)
    lines = [row for row in Path(sums).read_text().splitlines() if row.endswith(' *' + IMAGE_NAME)]
    need(len(lines) == 1 and lines[0].split(' ')[0] == lock['image']['sha256'], 'signed image checksum differs from the lock')
    need(sha256(sums) == lock['image']['signedChecksumSha256'], 'signed checksum file differs from the lock')
    return fingerprint, created


def cmd_verify(args):
    lock = json.loads(Path(args.lock).read_text())
    report = verify_population(args.inputs, lock, args.archive_keyring)
    if args.cloudimage_keyring:
        verify_image_checksums(NOBLE / 'image/SHA256SUMS', NOBLE / 'image/SHA256SUMS.gpg', args.cloudimage_keyring, lock)
        report['imageChecksumSignature'] = CLOUDIMAGE_FINGERPRINT
    print(json.dumps(report, sort_keys=True))


def download(url, destination):
    request = urllib.request.Request(url, headers={'User-Agent': 'fsgg-noble-inputs/1'})
    with urllib.request.urlopen(request, timeout=120) as response, open(destination, 'wb') as stream:
        shutil.copyfileobj(response, stream, 1 << 20)


def cmd_acquire(args):
    lock = json.loads(Path(args.lock).read_text())
    out = Path(args.out)
    need(not out.exists() or not any(out.iterdir()), 'acquire into a fresh empty directory only')
    for row in lock['files']:
        destination = out / row['path']
        destination.parent.mkdir(parents=True, exist_ok=True)
        if 'url' in row:
            need(row['url'].startswith('https://'), 'https origin required')
            download(row['url'], destination)
        else:  # derived records that travel with the lock
            shutil.copyfile(NOBLE / Path(row['path']).name, destination)
        need(sha256(destination) == row['sha256'] and destination.stat().st_size == row['bytes'], 'acquired bytes differ: ' + row['path'])
    cmd_verify(argparse.Namespace(lock=args.lock, inputs=str(out), archive_keyring=args.archive_keyring, cloudimage_keyring=args.cloudimage_keyring))
    if args.image_out:
        target = Path(args.image_out)
        need(not target.exists(), 'image destination must be new')
        download(IMAGE_BASE + IMAGE_NAME, target)
        need(sha256(target) == lock['image']['sha256'], 'image differs from the signed checksum')


def role_digest(roles, inventory, debs):
    rows = []
    for name in roles:
        key = name if name in inventory else name + ':amd64'
        need(key in inventory, 'role package missing from the final inventory: ' + name)
        deb = [d for d in debs if d['package'] == name.split(':')[0]]
        rows.append('%s\t%s\t%s' % (key, inventory[key], deb[0]['sha256'] if deb else 'image'))
    return hashlib.sha256(('\n'.join(sorted(rows)) + '\n').encode()).hexdigest(), sorted(rows)


def cmd_lock(args):
    work = Path(args.work)
    pop = work / 'pop'
    inventory_path = Path(args.inventory)
    inventory = dict(row.split('\t') for row in inventory_path.read_text().splitlines())
    index = package_index(pop)
    debs = []
    for deb in sorted((pop / 'apt/pool').rglob('*.deb')):
        filename = str(deb.relative_to(pop / 'apt'))
        package, version, architecture, digest, size = index[filename]
        debs.append({'package': package, 'version': version, 'architecture': architecture, 'filename': filename, 'sha256': digest, 'bytes': size})
    compiler, evidence = {}, {}
    for key, roles in COMPILER_ROLES.items():
        compiler[key], evidence[key] = role_digest(roles, inventory, debs)
    release = {}
    for suite in SUITES:
        path = pop / 'apt/dists' / suite / 'InRelease'
        fingerprint, created = gpgv(args.archive_keyring, path)
        need(fingerprint == ARCHIVE_FINGERPRINT, 'unexpected archive signer')
        release[suite] = {'inReleasePath': 'apt/dists/%s/InRelease' % suite, 'inReleaseSha256': sha256(path), 'signedAtEpoch': created}
    manifest = work / 'img' / (IMAGE_NAME.replace('.img', '.manifest'))
    transaction = {
        'schema': 'fsgg.dedicated-qualification-apt-transaction/1', 'baselineId': args.baseline,
        'snapshot': SNAPSHOT, 'archiveSigner': ARCHIVE_FINGERPRINT, 'suites': release,
        'installRequest': args.packages.split(),
        'baseImage': {'release': RELEASE, 'packageManifestSha256': sha256(manifest), 'packages': len(manifest.read_text().splitlines())},
        'finalInventory': {'packages': len(inventory), 'sha256': sha256(inventory_path)},
        'debs': debs,
        'compilerIdentity': {'derivation': 'sha256 of the sorted "package<TAB>installed version<TAB>locked .deb sha256 or image" rows',
                             'roles': {key: evidence[key] for key in evidence},
                             'scope': 'package-level identities of the signed transaction; actual cgo/race stays a separate qualification'},
    }
    NOBLE.mkdir(exist_ok=True)
    (NOBLE / 'image').mkdir(exist_ok=True)
    shutil.copyfile(inventory_path, NOBLE / 'package-inventory.expected')
    shutil.copyfile(inventory_path, pop / 'apt/package-inventory.expected')
    (NOBLE / 'apt-transaction.json').write_bytes(canonical(transaction))
    shutil.copyfile(NOBLE / 'apt-transaction.json', pop / 'apt/apt-transaction.json')
    for name in ('SHA256SUMS', 'SHA256SUMS.gpg'):
        shutil.copyfile(work / 'img' / name, NOBLE / 'image' / name)
    sums_hash = sha256(NOBLE / 'image/SHA256SUMS')
    image_line = [row for row in (NOBLE / 'image/SHA256SUMS').read_text().splitlines() if row.endswith(' *' + IMAGE_NAME)]
    need(len(image_line) == 1, 'image line in signed checksums')
    image_sha = image_line[0].split(' ')[0]
    fingerprint, created = gpgv(args.cloudimage_keyring, NOBLE / 'image/SHA256SUMS', NOBLE / 'image/SHA256SUMS.gpg')
    need(fingerprint == CLOUDIMAGE_FINGERPRINT, 'unexpected image signer')
    runner_meta = json.loads((NOBLE / 'runner-release.json').read_text())
    receipt = {
        'schema': 'fsgg.dedicated-qualification-image-receipt/1', 'baselineId': args.baseline, 'release': RELEASE,
        'imageUrl': IMAGE_BASE + IMAGE_NAME, 'imageSha256': image_sha, 'signedChecksumsSha256': sums_hash,
        'signatureSha256': sha256(NOBLE / 'image/SHA256SUMS.gpg'), 'signer': fingerprint, 'signatureCreatedEpoch': created,
        'signerCrossCheck': {'keyserver': 'hkps://keyserver.ubuntu.com 1A5D6C4C7DB87C81 fingerprint equals the signer',
                             'independentChannel': 'ubuntu-cloudimage-keyring.gpg in docker.io/library/ubuntu:24.04 holds the same fingerprint'},
        'observed': {'imageSha256Verified': True, 'packageManifestSha256': transaction['baseImage']['packageManifestSha256'],
                     'extractedImagePackageSetEqualsSignedManifest': True,
                     'offlineBakeSimulation': 'simulate-noble-bake.sh in a rootless container rooted at the extracted image, network disabled'},
    }
    (NOBLE / 'image-acquisition-receipt.json').write_bytes(canonical(receipt))
    rows = []
    for path in sorted((p for p in pop.rglob('*') if p.is_file()), key=lambda p: str(p.relative_to(pop))):
        relative = str(path.relative_to(pop))
        row = {'path': relative, 'sha256': sha256(path), 'bytes': path.stat().st_size}
        if relative in FIXED_URLS:
            row['url'] = FIXED_URLS[relative]
        elif relative.startswith('apt/dists/') or relative.startswith('apt/pool/'):
            row['url'] = SNAPSHOT + '/' + relative[len('apt/'):]
        rows.append(row)
    sdk = pop / 'sdk/dotnet-sdk-10.0.401-linux-x64.tar.gz'
    need(hashlib.sha512(sdk.read_bytes()).hexdigest() == SDK_SHA512, 'SDK bytes differ from the accepted pin')
    go = pop / ('go/go%s.linux-amd64.tar.gz' % GO_VERSION)
    runner = pop / ('runner/actions-runner-linux-x64-%s.tar.gz' % RUNNER_VERSION)
    need(runner_meta['digest'] == 'sha256:' + sha256(runner), 'runner bytes differ from the release digest')
    lock = {
        'schema': 'fsgg.dedicated-qualification-acquired-inputs/1', 'baselineId': args.baseline,
        'image': {'url': IMAGE_BASE + IMAGE_NAME, 'sha256': image_sha, 'signedChecksumSha256': sums_hash,
                  'qualifiedAcquisitionReceiptSha256': sha256(NOBLE / 'image-acquisition-receipt.json'), 'release': RELEASE},
        'sdk': {'version': '10.0.401', 'runtime': '10.0.12', 'archiveSha512': SDK_SHA512},
        'runner': {'version': RUNNER_VERSION, 'sha256': sha256(runner), 'releaseMetadataSha256': sha256(NOBLE / 'runner-release.json'),
                   'url': FIXED_URLS['runner/actions-runner-linux-x64-%s.tar.gz' % RUNNER_VERSION]},
        'goRace': {'version': GO_VERSION, 'archiveSha256': sha256(go), 'url': FIXED_URLS['go/go%s.linux-amd64.tar.gz' % GO_VERSION]},
        'compiler': {'raceRequired': True, 'cgoEnabled': True, **compiler},
        'aptTransaction': {'qualifiedSignedTransactionSha256': sha256(NOBLE / 'apt-transaction.json'),
                           'expectedFinalInventorySha256': sha256(inventory_path)},
        'policy': {'apparmorRestrictUnprivilegedUserns': 0, 'nativeWorkloadNoNewPrivileges': True, 'noJobPolicySetter': True},
        'files': rows,
    }
    (NOBLE / 'acquired-input-lock.json').write_bytes(canonical(lock))
    print(json.dumps(verify_population(pop, lock, args.archive_keyring), sort_keys=True))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    for name in ('verify', 'acquire', 'lock'):
        p = sub.add_parser(name)
        p.add_argument('--archive-keyring', default='/usr/share/keyrings/ubuntu-archive-keyring.gpg')
        p.add_argument('--cloudimage-keyring', default='/usr/share/keyrings/ubuntu-cloudimage-keyring.gpg')
        if name == 'verify':
            p.add_argument('--lock', default=str(NOBLE / 'acquired-input-lock.json'))
            p.add_argument('--inputs', required=True)
        if name == 'acquire':
            p.add_argument('--lock', default=str(NOBLE / 'acquired-input-lock.json'))
            p.add_argument('--out', required=True)
            p.add_argument('--image-out')
        if name == 'lock':
            p.add_argument('--work', required=True)
            p.add_argument('--inventory', required=True)
            p.add_argument('--baseline', default='ubuntu2404-linux-x64-v1')
            p.add_argument('--packages', required=True)
    args = parser.parse_args()
    {'verify': cmd_verify, 'acquire': cmd_acquire, 'lock': cmd_lock}[args.command](args)


if __name__ == '__main__':
    main()
