// Test-only isolated module loader; production source and installed caches are never rewritten.
const fs=require('fs'),vm=require('vm'),path=require('path'),assert=require('assert'),Module=require('module');
const root=process.argv[2],source=root+'/source';
const actual=Module.createRequire(source+'/dist/src/verify.js');const {left,right}=actual('@sweet-monads/either');
async function exercise(file,patched,mode){
 let compiles=0,tlcs=0;const errors=[{code:'CONTROLLED',message:'retained structured compiler error',reference:1n}];
 const prev={args:{serverEndpoint:{hostname:'127.0.0.1',port:19820},apalacheVersion:'0.56.1',invariant:'safety'},main:'Controlled',mainModule:{id:1n,name:'Controlled',declarations:[]},table:new Map(),modes:new Map(),errors:[],stage:'compiled',status:'ok'};
 const verifying={...prev,stage:'verifying'};
 const deps={
  './ir/initToPredicate':{convertInit:m=>right(m)},
  './compileToTlaplus':{compileToTlaplus:async()=>{compiles++;return mode==='compile-error'?left({explanation:'EXPLANATION',errors}):right('controlled TLA source');}},
  './apalache':{fetchApalache:async()=>right('controlled distribution')},
  './tlc':{loadTlcConfig:()=>({}),verify:async()=>{tlcs++;return mode==='tlc-error'?left({explanation:'original TLC failure',errors,isViolation:false}):right(undefined);}}
 };
 const module={exports:{}};const context={module,exports:module.exports,require:id=>Object.hasOwn(deps,id)?deps[id]:actual(id),Date,console};
 vm.runInNewContext(fs.readFileSync(file,'utf8'),context,{filename:file});
 const value=await module.exports.verifyWithTlcBackend(prev,verifying,0);
 assert.equal(compiles,1);
 if(mode==='compile-error') {assert(value.isLeft());assert.equal(value.value.msg,patched?'EXPLANATION':'error');assert.strictEqual(value.value.stage.errors,errors);assert.equal(tlcs,0);}
 else if(mode==='tlc-error'){assert(value.isLeft());assert.equal(value.value.msg,'original TLC failure');assert.strictEqual(value.value.stage.errors,errors);assert.equal(tlcs,1);}
 else{assert(value.isRight());assert.equal(value.value.status,'ok');assert.equal(value.value.errors.length,0);assert.equal(tlcs,1);}
 return {patched,mode,compiles,tlcs,outcome:value.isLeft()?'left':'right',message:value.isLeft()?value.value.msg:null,errorArrayIdentityPreserved:mode==='success'?null:value.value.stage.errors===errors,passed:true};
}
(async()=>{const result=[];for(const [file,patched]of[[root+'/old-dist/src/verify.js',false],[source+'/dist/src/verify.js',true]])for(const mode of['compile-error','success','tlc-error'])result.push(await exercise(file,patched,mode));console.log(JSON.stringify({scope:'production function with test-only dependencies; no model/JVM',controls:result},null,2));})().catch(e=>{console.error(e);process.exitCode=1});
