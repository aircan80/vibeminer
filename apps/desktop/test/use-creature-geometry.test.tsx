import {StrictMode,useState} from 'react';
import {act,cleanup,fireEvent,render,screen,waitFor} from '@testing-library/react';
import {afterEach,expect,it,vi} from 'vitest';
import '@testing-library/jest-dom/vitest';
import ready from '../../../contracts/examples/creature-geometry-ready.json';
import type {CreatureGeometryResult} from '../../../contracts/generated/discovery';
import type {DiscoveryBridge,BridgeReply} from '../src/shared/bridge';
import {captureGeometryAnchor,type GeometryIntent} from '../src/renderer/geometry-state';
import {useCreatureGeometry,type GeometryFlow} from '../src/renderer/useCreatureGeometry';
const ok=<T,>(value:T):BridgeReply<T>=>({ok:true,value});
const result={...ready,state:'noDeclaredTriangles',manifest:null} as CreatureGeometryResult;
const intent:GeometryIntent={selection:{...result.context,artifactId:result.artifactId,creatureId:result.creatureId,slot:result.slot},anchor:captureGeometryAnchor(result.opening,result.metadata),label:{name:'Authored Creature',buildVersion:'Authored build'},origin:{key:'exact',invoker:null,windowScroll:[0,0],innerScroll:[]}};
afterEach(cleanup);
it('StrictMode constructors/subscriptions perform no work and deliberate entry auto-reads Q0 once without a body',async()=>{
 const off=vi.fn(),bridge={onProgress:vi.fn(()=>off),onGeometryRetirement:vi.fn(()=>off),geometryViewEnter:vi.fn(async p=>ok({viewId:'a'.repeat(32),viewGeneration:p.viewGeneration})),creatureGeometryRead:vi.fn(async()=>ok(result)),creatureGeometryPrepare:vi.fn(),geometryViewOpen:vi.fn(),geometryViewSceneDisposed:vi.fn(async()=>ok({accepted:true})),geometryViewRelease:vi.fn(async()=>ok({released:true}))} as unknown as DiscoveryBridge;
 let flow!:GeometryFlow;const reserve=vi.fn(()=>vi.fn());function Harness(){flow=useCreatureGeometry(bridge,{tryReserve:reserve,revision:0});return <><button onClick={()=>flow.session.enter(intent)}>Inspect source geometry</button><p>{flow.snapshot.view?.message??'Not entered'}</p></>;}
 const rendered=render(<StrictMode><Harness/></StrictMode>);expect(bridge.geometryViewEnter).not.toHaveBeenCalled();fireEvent.click(screen.getByRole('button',{name:'Inspect source geometry'}));await screen.findByText('Declared main-view sections reference no triangles');await waitFor(()=>expect(bridge.geometryViewRelease).toHaveBeenCalledTimes(1));expect(bridge.creatureGeometryRead).toHaveBeenCalledTimes(1);expect(bridge.creatureGeometryPrepare).not.toHaveBeenCalled();expect(bridge.geometryViewOpen).not.toHaveBeenCalled();expect(reserve).not.toHaveBeenCalled();rendered.unmount();await act(async()=>flow.session.shutdown('applicationExit'));
});
it('subscriber unmount releases the presentation but leaves started Prepare and its progress listener owned',async()=>{
 let complete!:(r:BridgeReply<CreatureGeometryResult>)=>void;const prepared=new Promise<BridgeReply<CreatureGeometryResult>>(r=>complete=r),off=vi.fn(),release=vi.fn();
 const missing={...result,state:'notPrepared',sources:[]} as CreatureGeometryResult;
 const bridge={onProgress:vi.fn(()=>off),onGeometryRetirement:vi.fn(()=>off),geometryViewEnter:vi.fn(async p=>ok({viewId:'a'.repeat(32),viewGeneration:p.viewGeneration})),creatureGeometryRead:vi.fn(async()=>ok(missing)),creatureGeometryPrepare:vi.fn(()=>prepared),geometryViewSceneDisposed:vi.fn(async()=>ok({accepted:true})),geometryViewRelease:vi.fn(async()=>ok({released:true})),cancel:vi.fn(async()=>ok({requested:true}))} as unknown as DiscoveryBridge;
 let flow!:GeometryFlow;const reserve=()=>release;function Harness(){flow=useCreatureGeometry(bridge,{tryReserve:reserve,revision:0});return <button onClick={()=>flow.session.enter(intent)}>Enter</button>;}
 const rendered=render(<Harness/>);fireEvent.click(screen.getByRole('button',{name:'Enter'}));await waitFor(()=>expect(bridge.creatureGeometryPrepare).toHaveBeenCalledTimes(1));rendered.unmount();await waitFor(()=>expect(bridge.geometryViewRelease).toHaveBeenCalledTimes(1));expect(bridge.cancel).not.toHaveBeenCalled();expect(release).not.toHaveBeenCalled();expect(off).not.toHaveBeenCalled();await act(async()=>{complete(ok(result));await prepared;});await waitFor(()=>expect(release).toHaveBeenCalledTimes(1));expect(off).toHaveBeenCalledTimes(2);await flow.session.shutdown('applicationExit');
});
it('refreshes the admission callback without polling on ordinary rerenders, and resumes only a real revision change',async()=>{
 const release=vi.fn(),reserve=vi.fn(),off=vi.fn();let available=false;
 const bridge={onProgress:()=>off,onGeometryRetirement:()=>off,geometryViewEnter:vi.fn(async p=>ok({viewId:'a'.repeat(32),viewGeneration:p.viewGeneration})),creatureGeometryRead:vi.fn(async()=>ok({...result,state:'notPrepared',sources:[]} as CreatureGeometryResult)),creatureGeometryPrepare:vi.fn(async()=>ok(result)),geometryViewSceneDisposed:vi.fn(async()=>ok({accepted:true})),geometryViewRelease:vi.fn(async()=>ok({released:true})),cancel:vi.fn(async()=>ok({requested:true}))} as unknown as DiscoveryBridge;
 let flow!:GeometryFlow;function Harness(){const[revision,setRevision]=useState(0),[other,setOther]=useState(0);flow=useCreatureGeometry(bridge,{tryReserve:()=>{reserve();return available?release:null;},revision});return <><button onClick={()=>flow.session.enter(intent)}>Enter</button><button onClick={()=>setOther(other+1)}>Rerender {other}</button><button onClick={()=>{available=true;setRevision(revision+1);}}>Capacity changed</button><p>{flow.snapshot.view?.message}</p></>;}
 const rendered=render(<Harness/>);fireEvent.click(screen.getByRole('button',{name:'Enter'}));await screen.findByText('Waiting for a Creature preparation credit');expect(reserve).toHaveBeenCalledTimes(1);fireEvent.click(screen.getByRole('button',{name:'Rerender 0'}));expect(reserve).toHaveBeenCalledTimes(1);expect(bridge.creatureGeometryPrepare).not.toHaveBeenCalled();fireEvent.click(screen.getByRole('button',{name:'Capacity changed'}));await waitFor(()=>expect(bridge.creatureGeometryPrepare).toHaveBeenCalledTimes(1));expect(reserve).toHaveBeenCalledTimes(2);await waitFor(()=>expect(release).toHaveBeenCalledTimes(1));rendered.unmount();await flow.session.shutdown('applicationExit');
});
