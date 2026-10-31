import {act,cleanup,fireEvent,render,screen,waitFor} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import {afterEach,beforeEach,expect,it,vi} from 'vitest';
import type {Build,DiscoveryPage,WorkspaceSnapshot} from '../../../contracts/generated/discovery';
import type {BridgeReply,DiscoveryBridge} from '../src/shared/bridge';
import type {EngineStatus} from '../src/shared/engine-status';
import {EngineRecovery} from '../src/renderer/EngineRecovery';
import pageFixture from '../../../contracts/examples/discovery-page.json';
import workspaceFixture from '../../../contracts/examples/inventory-workspace.json';
import {catalog} from './catalog-page';
// D119: the app-wide stopped-engine state and what a restart reloads.
const success=<T,>(value:T):BridgeReply<T>=>({ok:true,value});
const notTested={ok:false as const,error:{code:'NOT_TESTED',message:'Outside this test.',retryable:false}};
const base=pageFixture.builds[0] as Build;
const build:Build={...base,sourceReceipts:[{...base.sourceReceipts[0],region:'us',role:'versions',sha256:'1'.repeat(64)},{...base.sourceReceipts[0],region:'us',role:'cdns',sha256:'2'.repeat(64),url:'https://metadata.invalid/cdns'}]};
const snapshot={...workspaceFixture,build,sourceReference:{...workspaceFixture.sourceReference,region:'us',versionsSha256:'1'.repeat(64),cdnsSha256:'2'.repeat(64)},configurations:[{...workspaceFixture.configurations[0],role:'buildConfig',key:build.buildConfig},{...workspaceFixture.configurations[1],role:'cdnConfig',key:build.cdnConfig}]} as WorkspaceSnapshot;
const running=(generation=1,revision=1):EngineStatus=>({state:'running',generation,revision,problem:null,restartable:false});
const stopped:EngineStatus={state:'stopped',generation:1,revision:2,problem:{code:'PROTOCOL_ERROR',message:'The local engine sent an invalid message.'},restartable:true};
function setup(){
  const listeners=new Set<(s:EngineStatus)=>void>();
  let finish!:(s:BridgeReply<EngineStatus>)=>void;
  const bridge:DiscoveryBridge={
    itemsData:vi.fn(async()=>notTested),itemsPrepare:vi.fn(async()=>notTested),itemsPage:vi.fn(async()=>notTested),itemInspect:vi.fn(async()=>notTested),
    products:vi.fn(async()=>success({products:[{id:'wow',label:'Retail',description:'World of Warcraft'}]})),
    read:vi.fn(async()=>notTested),refresh:vi.fn(async()=>notTested),
    readAll:vi.fn(async params=>success(catalog({...pageFixture,builds:!params.productId||params.productId==='wow'?[build]:[]} as DiscoveryPage,{productId:params.productId??null}))),
    refreshAll:vi.fn(async()=>notTested),open:vi.fn(async()=>success(snapshot)),
    tableData:vi.fn(async()=>success({snapshot:null})),tablePrepare:vi.fn(async()=>notTested),tableRows:vi.fn(async()=>notTested),tablesCatalog:vi.fn(async()=>notTested),tableDefinition:vi.fn(async()=>notTested),
    download:vi.fn(async()=>notTested),inventory:vi.fn(async()=>notTested),inventoryFiles:vi.fn(async()=>notTested),inventoryFile:vi.fn(async()=>notTested),
    cancel:vi.fn(async()=>success({requested:true})),onProgress:vi.fn(()=>()=>{}),
    engineStatus:vi.fn(async()=>success(running())),
    restartEngine:vi.fn(()=>new Promise<BridgeReply<EngineStatus>>(resolve=>{finish=resolve;})),
    onEngineStatus:vi.fn(listener=>{listeners.add(listener);return()=>{listeners.delete(listener);};}),
  };
  render(<EngineRecovery bridge={bridge}/>);
  return{bridge,push:(s:EngineStatus)=>act(()=>{for(const l of listeners)l(s);}),finish:(s:EngineStatus)=>act(async()=>{finish(success(s));})};
}
beforeEach(()=>{
  localStorage.clear();vi.spyOn(window,'scrollTo').mockImplementation(()=>{});Element.prototype.scrollIntoView=vi.fn();
  HTMLDialogElement.prototype.showModal=function(){this.setAttribute('open','');};HTMLDialogElement.prototype.close=function(){this.removeAttribute('open');};
});
afterEach(()=>{cleanup();vi.restoreAllMocks();});
it('shows one stopped state with the code, restarts only on request and reopens the build that was open',async()=>{
  const{bridge,push,finish}=setup();
  fireEvent.click(await screen.findByRole('option',{name:/Identity dddddddddd/}));fireEvent.click(screen.getByRole('button',{name:'Open build'}));
  await screen.findByRole('heading',{name:'Build overview'});
  expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
  push(stopped);
  const dialog=await screen.findByRole('alertdialog',{name:'Engine stopped after an invalid reply'});
  expect(dialog).toHaveTextContent('PROTOCOL_ERROR');expect(dialog).toHaveTextContent('The local engine sent an invalid message.');
  const restart=screen.getByRole('button',{name:'Restart engine'});expect(restart).toHaveFocus();
  // Escape doesn't dismiss it: nothing behind it works until the engine runs again.
  fireEvent(dialog,new Event('cancel',{cancelable:true}));expect(dialog).toBeInTheDocument();
  expect(bridge.restartEngine).not.toHaveBeenCalled();
  fireEvent.click(restart);
  push({...stopped,state:'restarting',restartable:false,revision:3});
  const busy=screen.getByRole('button',{name:'Restarting engine…'});
  expect(busy).toHaveAttribute('aria-disabled','true');expect(busy).toHaveFocus();
  fireEvent.click(busy);expect(bridge.restartEngine).toHaveBeenCalledTimes(1);
  // An older status that arrives late never undoes a newer one.
  push(stopped);expect(screen.getByRole('button',{name:'Restarting engine…'})).toBeInTheDocument();
  await finish(running(2,5));
  await waitFor(()=>expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
  // The fresh app opens the same build again through the normal Open.
  await waitFor(()=>expect(bridge.open).toHaveBeenCalledTimes(2));
  expect(vi.mocked(bridge.open).mock.calls[1][0]).toEqual({productId:build.productId,buildIdentity:build.id,sourceRegion:'us'});
  await screen.findByRole('heading',{name:'Build overview'});
});
it('offers nothing for a stop a restart cannot fix, and names a failed restart',async()=>{
  const{push}=setup();await screen.findByRole('option',{name:/Identity dddddddddd/});
  push({state:'stopped',generation:1,revision:2,problem:{code:'CATALOG_RESET_REQUIRED',message:'Refused.'},restartable:false});
  expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
  push({state:'stopped',generation:2,revision:3,problem:{code:'ENGINE_RESTART_FAILED',message:'The stopped engine did not exit.'},restartable:true});
  const dialog=await screen.findByRole('alertdialog',{name:'The engine didn’t restart'});
  expect(dialog).toHaveTextContent('ENGINE_RESTART_FAILED');expect(screen.getByRole('button',{name:'Restart engine'})).toBeInTheDocument();
});
it('reopens Discovery with focus on its heading when no build was open',async()=>{
  const{bridge,push,finish}=setup();await screen.findByRole('option',{name:/Identity dddddddddd/});
  push(stopped);fireEvent.click(await screen.findByRole('button',{name:'Restart engine'}));
  await finish(running(2,3));
  await waitFor(()=>expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
  await waitFor(()=>expect(document.activeElement?.tagName).toMatch(/^H[12]$/));
  expect(bridge.open).not.toHaveBeenCalled();expect(bridge.readAll).toHaveBeenCalledTimes(2);
});
