// @vitest-environment node
import {EventEmitter} from 'node:events';
import {PassThrough,Writable} from 'node:stream';
import {it,expect,vi} from 'vitest';
import {EngineClient,EngineFailure,type EngineProcess} from '../src/main/engine-client';
import {EngineSupervisor,type EngineSession} from '../src/main/engine-supervisor';
import {validEngineStatus,type EngineStatus} from '../src/shared/engine-status';
// D119: an invalid reply still stops the engine; only a user-triggered restart starts a new process.
class Child extends EventEmitter implements EngineProcess{
  stdout=new PassThrough();stderr=new PassThrough();requests:{id:string;method:string}[]=[];exitOnKill=true;
  stdin=new Writable({write:(chunk,_e,done)=>{for(const line of chunk.toString().split('\n').filter(Boolean))this.requests.push(JSON.parse(line));done();}});
  kill=vi.fn(()=>{if(this.exitOnKill)setImmediate(()=>this.emit('exit',null));return true;});
  send(value:object){this.stdout.write(JSON.stringify({protocolVersion:1,...value})+'\n');}
  async answerHello(){await vi.waitFor(()=>expect(this.requests.some(r=>r.method==='hello')).toBe(true));this.send({id:this.requests.find(r=>r.method==='hello')!.id,result:{protocolVersion:1,engineVersion:'authored',capabilities:[]}});}
}
const products={products:[{id:'wow',label:'Retail',description:'Authored'}]};
type Session=EngineSession&{engine:EngineClient;child:Child};
function supervised(){
  const children:Child[]=[],statuses:EngineStatus[]=[];
  const start=vi.fn(():Session=>{const child=new Child(),engine=new EngineClient(child),ready=engine.hello();void ready.catch(()=>{});children.push(child);return{engine,child,ready,retire:async()=>{await engine.shutdown();await engine.waitForExit(1000);return engine.hasExited;}};});
  const supervisor=new EngineSupervisor(start);supervisor.on('status',(s:EngineStatus)=>statuses.push(s));
  return{supervisor,children,start,statuses};
}
async function stopWithInvalidReply(){
  const s=supervised();await s.children[0].answerHello();await s.supervisor.current.ready;
  const first=s.supervisor.current.engine.request('products.list',{},'one').catch(e=>e as EngineFailure);
  s.children[0].send({id:'one',result:{products:'not a list'}});
  expect(await first).toMatchObject({code:'PROTOCOL_ERROR'});
  return s;
}
it('reports an invalid reply as a restartable stop and never restarts by itself',async()=>{
  const s=await stopWithInvalidReply();
  expect(s.supervisor.status).toMatchObject({state:'stopped',generation:1,problem:{code:'PROTOCOL_ERROR'},restartable:true});
  expect(validEngineStatus(s.supervisor.status)).toBe(true);
  expect(s.children[0].kill).toHaveBeenCalled();
  // Every later call still fails on the stopped process, and nothing starts a second one.
  await expect(s.supervisor.call(({engine})=>engine.request('products.list',{}))).rejects.toMatchObject({code:'PROTOCOL_ERROR'});
  await new Promise(r=>setTimeout(r,50));
  expect(s.start).toHaveBeenCalledTimes(1);
});
it('restarts once on request: the old process exits first, then a new one answers later requests',async()=>{
  const s=await stopWithInvalidReply();
  const restart=s.supervisor.restart(),again=s.supervisor.restart();
  expect(again).toBe(restart);
  expect(s.supervisor.status.state).toBe('restarting');
  await vi.waitFor(()=>expect(s.children).toHaveLength(2));
  expect(s.children[0].listenerCount('exit')).toBeGreaterThan(0);
  await s.children[1].answerHello();
  expect(await restart).toMatchObject({state:'running',generation:2,problem:null,restartable:false});
  expect(s.statuses.map(x=>x.state)).toEqual(['stopped','restarting','restarting','running']);
  expect(s.statuses.map(x=>x.revision)).toEqual([2,3,4,5]);
  const later=s.supervisor.call(({engine})=>engine.request('products.list',{},'two'));
  await vi.waitFor(()=>expect(s.children[1].requests.some(r=>r.id==='two')).toBe(true));
  s.children[1].send({id:'two',result:products});
  await expect(later).resolves.toEqual(products);
  // A running engine has nothing to restart.
  await expect(s.supervisor.restart()).resolves.toMatchObject({state:'running',generation:2});
  expect(s.start).toHaveBeenCalledTimes(2);
});
it('ignores a late reply and progress from the old engine after a restart',async()=>{
  const s=await stopWithInvalidReply();const old=s.supervisor.current;
  // Work begun on the old process that settles after the restart is dropped, not handed over as current.
  let settle!:(v:unknown)=>void;const pending=s.supervisor.call(()=>new Promise(r=>{settle=r;}));
  const restart=s.supervisor.restart();await vi.waitFor(()=>expect(s.children).toHaveLength(2));await s.children[1].answerHello();await restart;
  settle(products);await expect(pending).rejects.toMatchObject({code:'ENGINE_RESTARTED'});
  // The same request id on the new engine is answered only by the new engine.
  const progress=vi.fn();old.engine.on('progress',progress);
  const current=s.supervisor.current.engine.request('products.list',{},'same-id');let done=false;void current.then(()=>{done=true;});
  await vi.waitFor(()=>expect(s.children[1].requests.some(r=>r.id==='same-id')).toBe(true));
  s.children[0].send({id:'same-id',result:{products:[{id:'stale',label:'Stale',description:'Old engine'}]}});
  s.children[0].send({type:'progress',requestId:'same-id',sequence:1,stage:'Old',region:null,completed:null,total:null,unit:null});
  await new Promise(r=>setTimeout(r,150));
  expect(done).toBe(false);expect(progress).not.toHaveBeenCalled();
  s.children[1].send({id:'same-id',result:products});
  await expect(current).resolves.toEqual(products);
  expect(s.supervisor.status).toMatchObject({state:'running',generation:2});
});
it('offers no restart for a refused catalog, which a new process would refuse the same way',async()=>{
  const s=supervised();s.children[0].emit('exit',3);
  await expect(s.supervisor.current.ready).rejects.toMatchObject({code:'CATALOG_RESET_REQUIRED'});
  expect(s.supervisor.status).toMatchObject({state:'stopped',restartable:false,problem:{code:'CATALOG_RESET_REQUIRED'}});
  await expect(s.supervisor.restart()).resolves.toMatchObject({state:'stopped',restartable:false});
  expect(s.start).toHaveBeenCalledTimes(1);
});
it('starts no new engine while the stopped one has not exited, and lets the user try again',async()=>{
  vi.useFakeTimers();
  try{
    const s=supervised();await s.children[0].answerHello();await s.supervisor.current.ready;
    s.children[0].exitOnKill=false;
    const first=s.supervisor.current.engine.request('products.list',{},'one').catch(()=>{});
    s.children[0].send({id:'one',result:{products:'not a list'}});await first;
    const restart=s.supervisor.restart();await vi.advanceTimersByTimeAsync(10000);
    expect(await restart).toMatchObject({state:'stopped',generation:1,problem:{code:'ENGINE_RESTART_FAILED'},restartable:true});
    expect(s.start).toHaveBeenCalledTimes(1);
    expect(s.children[0].kill).toHaveBeenCalledWith('SIGKILL');
  }finally{vi.useRealTimers();}
});
it('starts no new engine when the app quits while a restart waits for the old one',async()=>{
  const s=await stopWithInvalidReply();const old=s.supervisor.current;
  let release!:(v:boolean)=>void;const retire=old.retire;old.retire=()=>new Promise<boolean>(r=>{release=r;}).then(async v=>{await retire();return v;});
  const restart=s.supervisor.restart();const closing=s.supervisor.close();
  release(true);
  await expect(closing).resolves.toBe(old);
  expect((await restart).state).not.toBe('running');
  expect(s.start).toHaveBeenCalledTimes(1);
  // Once closing, a restart request does nothing either.
  await expect(s.supervisor.restart()).resolves.toMatchObject({generation:1});
  expect(s.start).toHaveBeenCalledTimes(1);
});
it('keeps a restart that fails to start restartable',async()=>{
  const s=await stopWithInvalidReply();
  const restart=s.supervisor.restart();await vi.waitFor(()=>expect(s.children).toHaveLength(2));
  s.children[1].emit('error',new Error('spawn failed'));
  expect(await restart).toMatchObject({state:'stopped',generation:2,problem:{code:'ENGINE_UNAVAILABLE'},restartable:true});
});
it('validates the status main sends over the bridge',()=>{
  expect(validEngineStatus({state:'running',generation:1,revision:1,problem:null,restartable:false})).toBe(true);
  expect(validEngineStatus({state:'stopped',generation:1,revision:2,problem:{code:'PROTOCOL_ERROR',message:'Bad.'},restartable:true})).toBe(true);
  for(const bad of [null,{},{state:'running',generation:0,revision:1,problem:null,restartable:false},{state:'running',generation:1,revision:1,problem:null,restartable:true},{state:'odd',generation:1,revision:1,problem:null,restartable:false},{state:'stopped',generation:1,revision:1,problem:{code:'bad code',message:'x'},restartable:true},{state:'stopped',generation:1,revision:1,problem:{code:'X',message:''},restartable:true},{state:'running',generation:1,revision:1,problem:null,restartable:false,extra:1}])expect(validEngineStatus(bad)).toBe(false);
});
