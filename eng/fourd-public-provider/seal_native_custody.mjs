#!/usr/bin/env node
import { createCipheriv, createDecipheriv, publicEncrypt, privateDecrypt, randomBytes, constants, createHash } from 'node:crypto';
import { openSync, readFileSync, writeFileSync, closeSync, fsyncSync, chmodSync } from 'node:fs';
const fail = m => { throw new Error(m); };
const canonical = v => Buffer.from(JSON.stringify(v, Object.keys(v).sort()) + '\n');
function exclusive(path, bytes) { const fd=openSync(path,'wx',0o600); try { writeFileSync(fd,bytes); fsyncSync(fd); } finally { closeSync(fd); } chmodSync(path,0o600); }
function aad(run,source,profile) { return Buffer.from(`fsgg-native-custody/1\0${run}\0${source}\0${profile}`); }
function seal(input, pub, output, run, source, profile) {
 const plain=readFileSync(input); if(plain.length<2||plain.length>16*1024*1024) fail('plaintext-size-refused');
 const key=randomBytes(32), nonce=randomBytes(12), associated=aad(run,source,profile);
 const cipher=createCipheriv('aes-256-gcm',key,nonce,{authTagLength:16}); cipher.setAAD(associated);
 const ciphertext=Buffer.concat([cipher.update(plain),cipher.final()]), tag=cipher.getAuthTag();
 const wrapped=publicEncrypt({key:readFileSync(pub),oaepHash:'sha256',padding:constants.RSA_PKCS1_OAEP_PADDING},key);
 exclusive(output,canonical({schema:'fsgg.telemetry.native-custody-capsule/1',algorithm:'AES-256-GCM+RSA-OAEP-SHA256',runNonce:run,sourceSha:source,profileSha256:profile,aadSha256:createHash('sha256').update(associated).digest('hex'),nonce:nonce.toString('base64'),tag:tag.toString('base64'),wrappedKey:wrapped.toString('base64'),ciphertext:ciphertext.toString('base64')})); key.fill(0); plain.fill(0);
}
function unseal(input, priv, output, run, source, profile) {
 const v=JSON.parse(readFileSync(input,'utf8')); if(v.schema!=='fsgg.telemetry.native-custody-capsule/1'||v.runNonce!==run||v.sourceSha!==source||v.profileSha256!==profile) fail('capsule-identity-refused');
 const associated=aad(run,source,profile); if(createHash('sha256').update(associated).digest('hex')!==v.aadSha256) fail('capsule-aad-refused');
 const key=privateDecrypt({key:readFileSync(priv),oaepHash:'sha256',padding:constants.RSA_PKCS1_OAEP_PADDING},Buffer.from(v.wrappedKey,'base64'));
 const decipher=createDecipheriv('aes-256-gcm',key,Buffer.from(v.nonce,'base64'),{authTagLength:16}); decipher.setAAD(associated); decipher.setAuthTag(Buffer.from(v.tag,'base64'));
 exclusive(output,Buffer.concat([decipher.update(Buffer.from(v.ciphertext,'base64')),decipher.final()])); key.fill(0);
}
const [mode,input,key,output,run,source,profile,...rest]=process.argv.slice(2); if(rest.length||!['seal','unseal'].includes(mode)||!input||!key||!output||!/^[a-z0-9][a-z0-9-]{7,63}$/.test(run||'')||!/^[0-9a-f]{40}$/.test(source||'')||!/^[0-9a-f]{64}$/.test(profile||'')) fail('usage-refused');
if(mode==='seal') seal(input,key,output,run,source,profile); else unseal(input,key,output,run,source,profile);
