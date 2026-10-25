// @vitest-environment node
import {it,expect,vi} from 'vitest';
import {mkdtemp,mkdir,writeFile,rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';import {join} from 'node:path';import {createHash} from 'node:crypto';
import {GeometryBinaryProtocol} from '../src/main/geometry-binary-protocol';
import {snapshotBodyManifest,sameBodyManifest,validBodyManifest} from '../src/shared/creature-geometry';
import {appearanceFixture,geometryCSharpGolden} from './geometry-fixtures';
const selected={productId:'wow',buildIdentity:'d'.repeat(64),sourceRegion:'us',researchLocale:'enUS' as const,artifactId:'b'.repeat(64),creatureId:'7',slot:0};
function request(url:string){const r=new Request(url);Object.defineProperty(r,'initiatorOrigin',{value:'file://'});return r;}
async function stored(mutate?:(m:ReturnType<typeof appearanceFixture>['manifest'])=>void){
 const f=appearanceFixture(),bytes=Buffer.from(f.buffer);f.manifest.sha256=createHash('sha256').update(bytes).digest('hex');mutate?.(f.manifest);
 const data=await mkdtemp(join(tmpdir(),'vibeminer-appearance-main-')),shard=join(data,'objects-v1',f.manifest.sha256.slice(0,2));await mkdir(shard,{recursive:true});await writeFile(join(shard,f.manifest.sha256.slice(2)),bytes);
 return{data,bytes,manifest:f.manifest,remove:()=>rm(data,{recursive:true,force:true})};
}
async function serve(f:Awaited<ReturnType<typeof stored>>){
 const close=vi.fn(async()=>{}),p=new GeometryBinaryProtocol({dataDirectory:f.data,openGeometry:async()=>({leaseId:'c'.repeat(32),manifest:JSON.parse(JSON.stringify(f.manifest))}),closeGeometry:close,hasEngineExited:()=>false});
 const g=p.issue({viewId:'a'.repeat(32),viewGeneration:1,operationToken:'1'.repeat(32),geometryArtifactId:f.manifest.artifactId},selected,snapshotBodyManifest(f.manifest),()=>true);
 const response=await p.handle(request(g.url));return{p,g,response,close};
}

it('serves a verified VMA1 body after checking its own header and SHA-256, then retires the lease',async()=>{
 const f=await stored();try{const {p,g,response,close}=await serve(f);
  expect(response.status).toBe(200);expect(Buffer.from(await response.arrayBuffer())).toEqual(f.bytes);expect(close).toHaveBeenCalledWith('c'.repeat(32));
  await p.bodyRetired(g.operationToken);expect(g.manifest.byteLength).toBe(f.bytes.length);
 }finally{await f.remove();}
});
it('refuses a VMA1 whose header disagrees with its manifest even when the SHA-256 matches',async()=>{
 const f=await stored(m=>{m.displayId=24;});try{const {response,close}=await serve(f);expect(response.status).toBe(502);expect(close).toHaveBeenCalled();}finally{await f.remove();}
});
it('keeps VMG1 and VMA1 manifests distinct and bounded',()=>{
 const lines=geometryCSharpGolden().manifest,surfaces=appearanceFixture().manifest;
 expect(validBodyManifest(lines)).toBe(true);expect(validBodyManifest(surfaces)).toBe(true);expect(sameBodyManifest(lines,surfaces)).toBe(false);
 expect(validBodyManifest({...surfaces,byteLength:10485761})).toBe(false);expect(validBodyManifest({...surfaces,texels:{...surfaces.texels,byteLength:17}})).toBe(false);
 expect(validBodyManifest({...surfaces,artifactRevision:'model-geometry-vmg1-v1'})).toBe(false);
});
