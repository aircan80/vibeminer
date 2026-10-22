import {createContext,useContext,useLayoutEffect,useRef,useState,type ReactNode} from 'react';
import type {Build,WorkspaceSnapshot} from '../../../../contracts/generated/discovery';
import {useDownloadActivity} from './DownloadActivity';
import {summaryLine} from './download-activity-state';
import {BuildHeader} from './BuildHeader';
import type {RetentionController} from './useRetention';
import './desktop-home.css';

// Desktop Home shell (docs/design/desktop-home): fixed window, compact build header with the area bar, one visible area, status bar.
// Text-relative breakpoints are measured here, as on Discovery, because Chromium does not always re-run rem container queries when only the root text size changes.
const MEDIUM_REM=78.4,COMPACT_REM=65.333333;
type Shell={compact:boolean;statusSlot:HTMLElement|null};
const ShellContext=createContext<Shell>({compact:false,statusSlot:null});
export function useShell(){return useContext(ShellContext);}
// Areas can add their own status bar facts through statusSlot (Tables names its definitions revision and admitted rows).
// The docked activity panel covers the side column or inspector (and the end of the Home pane head), which are inert while it is open.
// In the compact layout, and on Items and Creatures whose own layouts don't reserve that column, the whole area is inert instead.
export function useCoveredSide(){const ui=useDownloadActivity(),{compact}=useShell();return !!ui?.expanded&&!compact;}
export type ShellArea='home'|'tables'|'files'|'items'|'creatures';
export function HomeShell({snapshot,catalogBuild,label,retention,onChoose,area,areas,status,fixtureLabel,children,dialog}:{snapshot:WorkspaceSnapshot;catalogBuild?:Build|null;label:string;retention?:RetentionController;onChoose:()=>void;area:ShellArea;areas:ReactNode;status?:ReactNode;fixtureLabel?:string|null;children:ReactNode;dialog?:ReactNode}){
 const host=useRef<HTMLDivElement>(null),toolbar=useRef<HTMLElement>(null),statusBar=useRef<HTMLElement>(null);
 const [size,setSize]=useState<{compact:boolean;medium:boolean}>({compact:false,medium:false}),[statusSlot,setStatusSlot]=useState<HTMLElement|null>(null);
 useLayoutEffect(()=>{
  const measure=()=>{const node=host.current;if(!node)return;const root=parseFloat(getComputedStyle(document.documentElement).fontSize)||15,width=node.clientWidth;if(width<=0)return;const next={compact:width<COMPACT_REM*root,medium:width<MEDIUM_REM*root};setSize(old=>old.compact===next.compact&&old.medium===next.medium?old:next);};
  measure();
  const resize=typeof ResizeObserver==='undefined'?null:new ResizeObserver(measure);if(host.current)resize?.observe(host.current);
  const text=new MutationObserver(measure);text.observe(document.documentElement,{attributes:true,attributeFilter:['style','class']});window.addEventListener('resize',measure);
  return()=>{resize?.disconnect();text.disconnect();window.removeEventListener('resize',measure);};
 },[]);
 useLayoutEffect(()=>{
  const place=()=>{const node=host.current;if(!node||!toolbar.current||!statusBar.current)return;node.style.setProperty('--dh-panel-top',`${toolbar.current.getBoundingClientRect().bottom}px`);node.style.setProperty('--dh-panel-bottom',`${statusBar.current.offsetHeight}px`);};
  place();const observer=typeof ResizeObserver==='undefined'?null:new ResizeObserver(place);if(toolbar.current)observer?.observe(toolbar.current);if(statusBar.current)observer?.observe(statusBar.current);window.addEventListener('resize',place);
  return()=>{observer?.disconnect();window.removeEventListener('resize',place);};
 },[]);
 const ui=useDownloadActivity(),summary=ui&&ui.entry&&!ui.expanded?ui.summary:null;
 return <ShellContext.Provider value={{compact:size.compact,statusSlot}}><div ref={host} className={`desktop-home${size.compact?' dh-compact dh-medium':size.medium?' dh-medium':''}`} data-area={area}>
  <a className="dh-skip" href="#dh-area">Skip to content</a>
  <div className="dh-app">
   <BuildHeader ref={toolbar} snapshot={snapshot} catalogBuild={catalogBuild} label={label} retention={retention} onChoose={onChoose} areas={areas} area={area}/>
   <main id="dh-area" className="dh-area" tabIndex={-1} inert={!!ui?.expanded&&(size.compact||area==='items'||area==='creatures')}>{children}</main>
   <footer className="dh-statusbar" ref={statusBar}>
    <span><strong>{snapshot.build.productId} {snapshot.build.version}</strong> · {snapshot.sourceReference.region.toUpperCase()} · enUS</span>
    {status}<span ref={setStatusSlot} className="dh-status-slot"/>
    {fixtureLabel&&<span className="dh-fixture"><strong>{fixtureLabel}</strong><span className="dh-secondary"> · Synthetic identities and receipts.</span></span>}
    <span className="dh-spacer"/>
    {summary&&<span className={ui!.attention?'dh-warn':'dh-busy'} aria-live="off">{!ui!.attention&&ui!.active&&<span className="dh-pulse" aria-hidden="true"/>}{summaryLine(summary)}</span>}
   </footer>
  </div>
  {dialog}
 </div></ShellContext.Provider>;
}
