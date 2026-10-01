#!/usr/bin/env python3
"""Validate settled installed-provider results and retain only hashes for public projection."""
from __future__ import annotations
import argparse, hashlib, json
from pathlib import Path
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def main():
 ap=argparse.ArgumentParser(); ap.add_argument('--private-root',type=Path,required=True); ap.add_argument('--facts',type=Path,required=True); ap.add_argument('--profile',type=Path,required=True); ap.add_argument('--command',type=Path,required=True); ap.add_argument('--source-revision',required=True); ap.add_argument('--source-tree',required=True); ap.add_argument('--output',type=Path,required=True); a=ap.parse_args()
 paths=[a.private_root/'evidence/execute-first.json',a.private_root/'evidence/execute-repeat.json',a.private_root/'evidence/recover.json']
 receipts=[json.loads(p.read_text()) for p in paths]
 if [r.get('outcome') for r in receipts]!=['completed','duplicate','duplicate']: raise ValueError('production outcome sequence differs')
 if len({r.get('commandId') for r in receipts})!=1 or any(r.get('cleanupCompleted') is not True for r in receipts): raise ValueError('production result did not settle one command')
 if len({r.get('operationOutputSha256') for r in receipts})!=1 or receipts[0].get('executionStarted') is not True: raise ValueError('one completed launch is not bound')
 journals=list((a.private_root/'journal-v1').glob('*.bin')); locks=list((a.private_root/'journal-v1').glob('*.lock'))
 if len(journals)!=1 or locks: raise ValueError('private journal is not one settled entry')
 facts=json.loads(a.facts.read_text())
 value={'schema':'fsgg.portable-python-provider-private-summary/1','sourceRevision':a.source_revision,'sourceTree':a.source_tree,'providerFactsSha256':sha(a.facts),'candidateReceiptSha256':receipts[0]['candidateReceiptSha256'],'profileSha256':sha(a.profile),'commandSha256':sha(a.command),'executeResultSha256':sha(paths[0]),'duplicateResultSha256':sha(paths[1]),'recoverResultSha256':sha(paths[2]),'journalSha256':sha(journals[0]),'oneLaunchObserved':True,'duplicateObserved':True,'recoverSettled':True}
 a.output.write_text(json.dumps(value,sort_keys=True,separators=(',',':'))+'\n')
if __name__=='__main__':
 try: main()
 except Exception as e: print('PORTABLE_PROVIDER_SUMMARY_REFUSED',e); raise SystemExit(2)
