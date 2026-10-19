import {useCallback,useLayoutEffect,useRef} from 'react';
import type {GeometryFlow} from './useCreatureGeometry';
import {geometryIntentKey,geometryMeasurement,type GeometryIntent,type GeometrySourceRoute} from './geometry-state';
import {CreatureGeometryEvidence} from './CreatureGeometryEvidence';
import type {GeometryCameraAction} from './geometry-camera';
import './creature-geometry.css';
const controls:readonly Readonly<{label:string;action:GeometryCameraAction}>[]= [
 {label:'Rotate left',action:{kind:'rotate',yaw:-Math.PI/12,elevation:0}},{label:'Rotate right',action:{kind:'rotate',yaw:Math.PI/12,elevation:0}},
 {label:'Rotate up',action:{kind:'rotate',yaw:0,elevation:Math.PI/12}},{label:'Rotate down',action:{kind:'rotate',yaw:0,elevation:-Math.PI/12}},
 {label:'Zoom in',action:{kind:'zoom',ticks:-1}},{label:'Zoom out',action:{kind:'zoom',ticks:1}},{label:'Reset view',action:{kind:'reset'}},
];
export function CreatureGeometryView({flow,onReturn,onSource}:{flow:GeometryFlow;onReturn:()=>void;onSource?:(source:GeometrySourceRoute)=>void}){
 const {snapshot,session}=flow,v=snapshot.view,heading=useRef<HTMLHeadingElement>(null),returnButton=useRef<HTMLButtonElement>(null),status=useRef<HTMLDivElement>(null),focusOwner=useRef<HTMLElement|null>(null),entry=useRef<GeometryIntent|null>(null),canvasCleanup=useRef<(()=>void)|null>(null);
 const generation=v?.generation;
 const canvas=useCallback((node:HTMLCanvasElement|null)=>{canvasCleanup.current?.();canvasCleanup.current=null;if(node&&generation!==undefined)canvasCleanup.current=session.attachCanvas(generation,node);},[session,generation]);
 useLayoutEffect(()=>{if(!v){entry.current=null;return;}if(entry.current!==v.intent){entry.current=v.intent;heading.current?.focus({preventScroll:true});}},[v?.intent]);
 useLayoutEffect(()=>{const old=focusOwner.current;if(old&&!old.isConnected){focusOwner.current=null;if(document.activeElement===document.body||document.activeElement===old)(returnButton.current??status.current)?.focus({preventScroll:true});}},[v?.phase,v?.canMount]);
 if(!v)return null;
 const op=snapshot.operation&&geometryIntentKey(snapshot.operation.intent)===geometryIntentKey(v.intent)?snapshot.operation:null,pending=!!op&&['pending','stopping'].includes(op.status),measurement=pending?geometryMeasurement(op.progress):null;
 const waiting=['entering','checking','waiting'].includes(v.phase),retry=v.phase==='outcome'&&(!v.error||v.error.retryable)&&(v.error!==null||v.result?.problem?.retryable===true||v.message==='Geometry preparation stopped');
 return <section className="creature-geometry" aria-label={`Creature ${v.intent.selection.creatureId} slot ${v.intent.selection.slot} geometry`} onFocusCapture={e=>{if((e.target as HTMLElement).matches('[data-geometry-focus-owner]'))focusOwner.current=e.target as HTMLElement;}} onBlurCapture={e=>{if(e.relatedTarget&&!e.currentTarget.contains(e.relatedTarget as Node))focusOwner.current=null;}}>
 <div className="geometry-heading"><button ref={returnButton} onClick={onReturn}>Return to selected display metadata</button><h2 ref={heading} tabIndex={-1}>Diagnostic source geometry</h2><p className="geometry-creature-name">{v.intent.label.name}</p><p>Creature {v.intent.selection.creatureId} · slot {v.intent.selection.slot} · display {v.intent.anchor.metadata.selected?.displayId}</p><p>Unposed source edges · appearance not evaluated</p><p>All declared main-view sections</p></div>
 <div ref={status} tabIndex={-1} className="geometry-status"><p role="status">{v.message}</p>{measurement&&<p aria-live="off">{measurement.label}</p>} {op?.progress?.step?.description&&pending&&<p>{op.progress.step.description}</p>}{v.error&&<p>{v.error.code}: {v.error.message}</p>}
 {pending&&<button data-geometry-focus-owner aria-disabled={op!.status==='stopping'} onClick={()=>session.stopPreparation(op!.id)}>{op!.status==='stopping'?'Stopping geometry preparation…':'Stop preparing geometry'}</button>}{waiting&&!pending&&<button data-geometry-focus-owner onClick={()=>session.stopPreparation()}>Stop waiting for geometry</button>}{retry&&!snapshot.retirement&&<button data-geometry-focus-owner onClick={()=>session.retry()}>Retry geometry</button>}</div>
 <div className={`geometry-layout${!v.canMount&&['outcome','blocked','releasing'].includes(v.phase)?' geometry-layout-text':''}`}>{!(!v.canMount&&['outcome','blocked','releasing'].includes(v.phase))&&<div className="geometry-drawing"><div className="geometry-viewport">{v.canMount&&<canvas ref={canvas} width={588} height={441} tabIndex={0} data-geometry-focus-owner aria-label={`Unposed geometry for Creature ${v.intent.selection.creatureId}, slot ${v.intent.selection.slot}; all declared sections`} aria-describedby="geometry-camera-help"/>}</div>{v.canMount&&<p id="geometry-camera-help" className="geometry-camera-help">When the canvas is focused: arrow keys rotate, +/− zoom, R resets. Tab leaves the canvas.</p>}{v.phase==='ready'&&<div className="geometry-controls" role="group" aria-label="Geometry camera">{controls.map(c=><button data-geometry-focus-owner key={c.label} onClick={()=>session.move(c.action)}>{c.label}</button>)}</div>}</div>}
 <CreatureGeometryEvidence inspectable={v.phase==='ready'} anchor={v.intent.anchor} result={v.result} sections={v.sections} sectionOffset={v.sectionOffset} onPage={offset=>session.sectionPage(offset)} onSource={onSource}/></div>
 </section>;
}
