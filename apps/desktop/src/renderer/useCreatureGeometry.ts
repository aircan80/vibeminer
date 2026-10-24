import {useCallback,useEffect,useLayoutEffect,useMemo,useRef,useState,useSyncExternalStore} from 'react';
import type {DiscoveryBridge} from '../shared/bridge';
import {GeometrySession} from './geometry-session';
import type {GeometryIntent,GeometryLongAdmission,GeometrySnapshot} from './geometry-state';
import type {GeometryCameraAction} from './geometry-camera';

/** Only commands cross React props. Each command looks up the current imperative owner at call time. */
export interface GeometryActions{
 enter(intent:GeometryIntent):void;retry():void;stopPreparation(requestId?:string):void;
 leave(reason:'return'|'navigation'|'source'|'unmount'):Promise<void>;
 attachCanvas(generation:number,canvas:HTMLCanvasElement):()=>void;
 iconsHiddenCommitted(generation:number):void;resizeCanvas(generation:number):void;move(action:GeometryCameraAction):void;sectionPage(offset:number):void;dismissActivity():void;
 shutdown(reason:'documentReplaced'|'bridgeClosing'|'applicationExit'):Promise<void>;
}
export function geometryActions(current:()=>GeometrySession|null):GeometryActions{
 return Object.freeze({enter:(intent:GeometryIntent)=>{current()?.enter(intent);},retry:()=>{current()?.retry();},stopPreparation:(id?:string)=>{current()?.stopPreparation(id);},leave:(reason:'return'|'navigation'|'source'|'unmount')=>current()?.leave(reason)??Promise.resolve(),attachCanvas:(generation:number,canvas:HTMLCanvasElement)=>current()?.attachCanvas(generation,canvas)??(()=>{}),iconsHiddenCommitted:(generation:number)=>{current()?.iconsHiddenCommitted(generation);},resizeCanvas:(generation:number)=>{current()?.resizeCanvas(generation);},move:(action:GeometryCameraAction)=>{current()?.move(action);},sectionPage:(offset:number)=>{current()?.sectionPage(offset);},dismissActivity:()=>{current()?.dismissActivity();},shutdown:(reason:'documentReplaced'|'bridgeClosing'|'applicationExit')=>current()?.shutdown(reason)??Promise.resolve()});
}
export type GeometryFlow=Readonly<{snapshot:GeometrySnapshot;session:GeometryActions}>;
/** One owner above build/area routes. The body-bearing manager lives solely in an imperative ref. */
export function useCreatureGeometry(bridge:DiscoveryBridge,admission:GeometryLongAdmission):GeometryFlow{
 const owner=useRef<{bridge:DiscoveryBridge;manager:GeometrySession}|null>(null),wantedBridge=useRef(bridge),switching=useRef<Promise<void>|null>(null),alive=useRef(false),[revision,setRevision]=useState(0);
 wantedBridge.current=bridge;
 if(!owner.current)owner.current={bridge,manager:new GeometrySession(bridge,admission)};
 const subscribe=useCallback((listener:()=>void)=>owner.current!.manager.subscribe(listener),[revision]);
 const read=useCallback(()=>owner.current!.manager.snapshot(),[revision]);
 const snapshot=useSyncExternalStore(subscribe,read,read);
 const actions=useMemo(()=>geometryActions(()=>owner.current?.manager??null),[]);
 useLayoutEffect(()=>{owner.current!.manager.updateAdmission(admission);},[admission.revision,admission.tryReserve,revision]);
 useEffect(()=>{
  alive.current=true;
  return()=>{alive.current=false;void owner.current?.manager.leave('unmount').catch(()=>{});};
 },[]);
 useEffect(()=>{
  if(owner.current!.bridge===bridge||switching.current)return;
  const prior=owner.current!;
  // A real bridge replacement closes the old session before a new body-bearing owner can exist.
  switching.current=prior.manager.shutdown('bridgeClosing').then(()=>{
   if(!alive.current)return;owner.current={bridge:wantedBridge.current,manager:new GeometrySession(wantedBridge.current,admission)};setRevision(value=>value+1);
  }).catch(()=>{/* The old charged cleanup debt remains the sole owner. */}).finally(()=>{switching.current=null;});
 },[bridge,revision]);
 return useMemo(()=>Object.freeze({snapshot,session:actions}),[snapshot,actions]);
}
