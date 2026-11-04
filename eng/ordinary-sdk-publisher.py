#!/usr/bin/env python3
"""Fresh SDK source controls; disabled admission refuses before credential loading."""
from __future__ import annotations
import argparse
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('ordinary_sdk_publisher',HERE/'ordinary_sdk_publisher.py')
P=importlib.util.module_from_spec(spec); spec.loader.exec_module(P)


def env(name):
    v=os.environ.get(name); P.require(bool(v),name+' is required'); return v


def child_env(root):
    result={k:os.environ[k] for k in ('PATH','DOTNET_ROOT','DOTNET_PROCESSOR_COUNT','DOTNET_NOLOGO') if k in os.environ}
    result.update(HOME=str(root/'home'),DOTNET_CLI_HOME=str(root/'home'),NUGET_PACKAGES=str(root/'nuget'),TMPDIR=str(root/'tmp'),DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_CLI_UI_LANGUAGE='en')
    for key in ('home','nuget','tmp'): (root/key).mkdir(mode=0o700,parents=True,exist_ok=True)
    return result


def execute(argv,root,*,timeout=120):
    return subprocess.run(argv,cwd=root,env=child_env(root),capture_output=True,timeout=timeout,check=True)


def signature(raw,root):
    path=root/'served-signature.nupkg';path.write_bytes(raw)
    result=execute(['dotnet','nuget','verify',str(path),'--all'],root,timeout=60)
    P.require(len(result.stdout)+len(result.stderr)<=65536,'signature output bound exceeded')
    report=(result.stdout+result.stderr).decode('utf-8',errors='replace').lower()
    P.require('repository' in report and 'https://api.nuget.org/v3/index.json' in report,'NuGet repository signature identity absent')


def public_load(s,context,directory,transport,root):
    raw=P.F.fetch_feed(transport,'',P.VERSION,'nuget',P.PACKAGE)
    P.require(raw is not None,'public replacement unavailable')
    with P.zipfile.ZipFile(P.io.BytesIO(raw)) as z: P.require('.signature.p7s' in z.namelist(),'public repository signature absent')
    signature(raw,root)
    expected=(directory/P.PACKAGE_FILE).read_bytes()
    P.require(P.F.payload_map(raw)==P.F.payload_map(expected),'public payload differs from A')
    config=root/'public.config';config.write_text('<configuration><packageSources><clear/><add key="public" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
    installed=root/'installed'
    execute(['dotnet','tool','install',P.PACKAGE,'--version',P.VERSION,'--tool-path',str(installed),'--configfile',str(config),'--no-cache'],root,timeout=300)
    package_root=installed/'.store'/P.PACKAGE.lower()/P.VERSION/P.PACKAGE.lower()/P.VERSION
    tools={n:d for n,d in P.F.payload_map(expected).items() if n.startswith('tools/') and not n.endswith('/')}
    P.require(bool(tools) and package_root.is_dir(),'installed public tool root absent')
    for name,digest in tools.items():
        path=package_root/name
        P.require(path.is_file() and not path.is_symlink() and P.sha(path.read_bytes())==digest,'installed public runtime differs: '+name)
    result=execute(['python3',str(HERE/'test-ordinary-sdk-profile.py'),'--tool',str(installed/'fsgg-coordination')],root)
    P.require(result.stdout==b'ORDINARY_SDK_PROFILE_FIXTURES passed=15 nativeEffects=0 githubTokenAbsent=true syntheticKeysOnly=true\n','actual public installed CLI controls differ')
    return P.canonical(dict(schema='fsgg.coordination.ordinary-sdk-public-load/1',contentId=P.content_id(s),publisher=context['sha'],runId=context['runId'],source=s['source'],packageSha256=s['candidate']['packageSha256'],servedArchiveSha256=P.sha(raw),signature='verified',installedCliCases=15,publicOnly=True,consumerAdoption=False))+b'\n'


def run(command,workdir,*,admission_path=None,original_run=None):
    admission=P.strict_json(Path(admission_path or P.ROOT/P.ADMISSION).read_bytes())
    selected=P.selection(admission)
    context=P.bind(selected,os.environ)
    mode=os.environ.get('SDK_PROFILE_MODE','publish')
    allowed={'preflight':{'bind','preflight'},'publish':{'bind','preflight','packages','public-load','qualify-finalize','finalize'},
             'observe':{'bind','qualify-original','observe'},'forward':{'bind','qualify-original','packages','public-load','qualify-finalize','finalize'}}
    P.require(command in allowed[mode],'stage outside selected SDK mode')
    if mode=='forward':P.forward_header(admission)
    if command=='bind':
        if os.environ.get('GITHUB_OUTPUT'):
            with open(os.environ['GITHUB_OUTPUT'],'a') as output:output.write('nuget_user='+selected['nugetUser']+'\n')
        print('SDK_PROFILE_BINDING_OK');return
    workdir=Path(workdir);workdir.mkdir(mode=0o700,parents=True,exist_ok=False)
    override=os.environ.get('FSGG_SHARED_HELPER_ROOT')
    P.require(not override or not os.environ.get('GITHUB_ACTIONS'),'hosted helper override refused')
    helper=Path(override) if override else P.F.fetch_shared_helper(admission['sharedHelper'],workdir/'helper')
    engine=P.F.load_engine(helper,admission['sharedHelper'])
    api_class=P.bounded_api(engine);token=env('GH_TOKEN');api=api_class(token)
    transport=P.F.UrllibTransport();P.native_context(api,selected,context)
    if mode=='forward':s=P.forward_selection(admission,selected,context,api)
    elif mode=='observe':
        original_run=original_run or int(env('SDK_ORIGINAL_RUN'))
        P.require(original_run!=context['runId'],'explicit original run must differ from current caller')
        original=api.get(f'repos/{P.REPO}/actions/runs/{original_run}')
        P.require(original.get('id')==original_run and original.get('repository',{}).get('id')==P.REPO_ID
                  and original.get('path')==P.WORKFLOW and original.get('run_attempt')==1
                  and original.get('event')=='workflow_dispatch' and original.get('head_branch')=='main'
                  and original.get('actor',{}).get('login')==selected['operator'],'original native caller differs')
        s=P.runtime_selection(selected,{'sha':original.get('head_sha'),'runId':original_run})
    else:s=P.runtime_selection(selected,context)
    directory=P.candidate(api,transport,token,s,workdir/'candidate')
    if command=='preflight':
        P.vacant(api,transport,token,s,engine.provider.NotFound)
        print('SDK_PROFILE_PREFLIGHT_OK no-effect/no-reservation');return
    if command=='qualify-original':print('SDK_PROFILE_ORIGINAL_INPUTS_OK no-effect');return
    if command=='qualify-finalize':
        P.acquire_public(api,transport,token,s,context)
        print('SDK_PROFILE_FINALIZE_INPUTS_OK no-effect');return
    if command=='public-load':
        receipt=public_load(s,context,directory,transport,workdir)
        (workdir/'ordinary-sdk-public-load.json').write_bytes(receipt)
        print('SDK_PROFILE_PUBLIC_LOAD_OK consumerAdoption=false');return
    ledger=api_class(env('ORDINARY_LEDGER_TOKEN'));ref=P.journal_ref(s)
    journal=engine.journal.ProtectedReleaseJournal(ledger,ref,main_directory=True)
    if command=='observe':
        state=journal.read();journal.validate_intent(P.intent(s));s={**s,'effectOwners':P.effect_owners(state)}
        provider=P.Provider(engine,api,s,directory,transport,stage='observe',github_token=token,signature=lambda raw:signature(raw,workdir))
        facts={e.identity:provider.observe(e).state for e in P.effects(engine,s,directory)}
        result={'originalRun':original_run,'originalPublisher':s['invocation']['publisher'],'recordedEffects':state.effects,
                'packageObservations':facts,'mutationAuthorized':False,'absencePermitsResend':False,'finalization':'unresolved; explicit original-forward admission required'}
        (workdir/'original-observation.json').write_bytes(P.canonical(result)+b'\n')
        print('SDK_PROFILE_ORIGINAL_OBSERVED no-write/no-resend');return
    fresh,physical=engine.route.classify_journal_destination(ledger,ref)
    if mode=='forward':
        P.require(not fresh,'forward requires retained original journal')
        state=journal.read();journal.validate_intent(P.intent(s))
        s=P.bind_checkpoint(s,state,admission['forward']['checkpoint'])
    readback=None
    if command=='finalize':
        current_proof=P.acquire_public(api,transport,token,s,context)
        frozen=s.get('effectOwners',{}).get('asset:readback')
        readback=P.acquire_public(api,transport,token,s,P.owner_context(frozen)) if frozen else current_proof
    planned=P.effects(engine,s,directory,readback)
    policy_api=api_class(env('POLICY_TOKEN')) if command=='finalize' else None
    gate=P.Admission(api,s,context,planned,command,policy_reader=(lambda:policy_api.get(f'repos/{P.REPO}/rulesets/21633398')) if policy_api else None)
    gate=engine.route.ProtectedPublisherAdmission(gate,ledger,ref)
    provider=P.Provider(engine,api,s,directory,transport,stage=command,github_token=token,
                        nuget_key=env('NUGET_API_KEY') if command=='packages' else '',runner=lambda argv:execute(argv,workdir,timeout=300),
                        signature=lambda raw:signature(raw,workdir),readback=readback)
    provider.final_effects=planned
    if command=='packages' and mode=='publish':
        P.require(fresh,'fresh package dispatch requires unused journal; preserve/reconcile original')
        P.vacant(api,transport,token,s,engine.provider.NotFound)
        P.require(gate.authorize(P.content_id(s),'journal','intent',P.content_id(s)),'journal admission refused')
        P.require(engine.route.authority_main(ledger)==physical,'Authority changed before genesis')
        journal.initialize(P.intent(s))
    elif command=='finalize':
        P.require(not fresh,'finalize requires original durable package custody')
        state=journal.read();journal.validate_intent(P.intent(s))
        P.require(all(state.effects.get(P.effect_id(s,e))=='verified' for e in P.EFFECTS[:2]),'package prefix incomplete/Unknown; no finalization')
        if mode!='forward':P.require(not(set(state.effects)-{P.effect_id(s,e) for e in P.EFFECTS[:2]}),'finalization already attempted; reconcile original without replay')
    if mode=='forward' and command=='packages' and set(state.effects)-{P.effect_id(s,e) for e in P.EFFECTS[:2]}:
        P.require(all(state.effects.get(P.effect_id(s,e))=='verified' for e in P.EFFECTS[:2]),'original finalization has incomplete package prefix')
        for effect in planned:engine.execution._observe(provider,effect)
        result={'status':'verified-package-prefix','sends':[],'contentId':P.content_id(s)}
    else:result=P.advance(engine,s,planned,journal,gate,provider,context)
    (workdir/'stage-result.json').write_bytes(P.canonical(result)+b'\n')
    print('SDK_PROFILE_STAGE_VERIFIED '+command)


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('command',choices=('bind','preflight','packages','public-load','finalize','observe','qualify-finalize','qualify-original'))
    p.add_argument('--workdir',type=Path)
    p.add_argument('--original-run',type=int)
    args=p.parse_args()
    P.require(args.command=='bind' or args.workdir is not None,'private fresh workdir required')
    run(args.command,args.workdir,original_run=args.original_run)


if __name__=='__main__':
    try: main()
    except Exception as error:
        # Never print subprocess argv/output or credential-bearing native exception text.
        reason=str(error) if isinstance(error,P.Refused) else type(error).__name__
        print('SDK_PROFILE_REFUSED '+reason,file=sys.stderr);sys.exit(3)
