import {createContext,useContext,useLayoutEffect,useRef,useState,type ReactNode} from 'react';
import type {Build,WorkspaceSnapshot} from '../../../../contracts/generated/discovery';
import {useDownloadActivity} from './DownloadActivity';
import {summaryLine} from './download-activity-state';
import {BuildHeader} from './BuildHeader';
import type {RetentionController} from './useRetention';
import './desktop-home.css';

// Desktop Home shell (docs/design/desktop-home): fixed window, compact build header with the area bar, one visible area, status bar.
// When the header and the area's 10rem minimum don't both fit, .dh-app scrolls as a whole (.dh-shell-scroll). The open activity panel is
// then fixed to the window above the status bar's height, within the shell's inner width, and the shell is locked while it is open.
// Whatever takes focus in the shell outside the panel, by keyboard or programmatically, is scrolled into the shell's view; the panel
// scrolls its own content.
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
 const host=useRef<HTMLDivElement>(null),shell=useRef<HTMLDivElement>(null),toolbar=useRef<HTMLElement>(null),statusBar=useRef<HTMLElement>(null),areaNode=useRef<HTMLElement>(null);
 const [size,setSize]=useState<{compact:boolean;medium:boolean}>({compact:false,medium:false}),[statusSlot,setStatusSlot]=useState<HTMLElement|null>(null),[scrolling,setScrolling]=useState(false);
 useLayoutEffect(()=>{
  const measure=()=>{const node=host.current;if(!node)return;const root=parseFloat(getComputedStyle(document.documentElement).fontSize)||15,width=node.clientWidth;if(width<=0)return;const next={compact:width<COMPACT_REM*root,medium:width<MEDIUM_REM*root};setSize(old=>old.compact===next.compact&&old.medium===next.medium?old:next);};
  measure();
  const resize=typeof ResizeObserver==='undefined'?null:new ResizeObserver(measure);if(host.current)resize?.observe(host.current);
  const text=new MutationObserver(measure);text.observe(document.documentElement,{attributes:true,attributeFilter:['style','class']});window.addEventListener('resize',measure);
  return()=>{resize?.disconnect();text.disconnect();window.removeEventListener('resize',measure);};
 },[]);
 useLayoutEffect(()=>{
  const place=(fromScroll=false)=>{const node=host.current,app=shell.current,area=areaNode.current;if(!node||!app||!area||!toolbar.current||!statusBar.current)return;
   const root=parseFloat(getComputedStyle(document.documentElement).fontSize)||15,shellBox=app.getBoundingClientRect();
   // Measured from the header and the minimum, not the content, so Home taking its natural height can't flip it back.
   const over=toolbar.current.offsetHeight+statusBar.current.offsetHeight+10*root>app.clientHeight+1;setScrolling(old=>old===over?old:over);
   node.style.setProperty('--dh-panel-top',`${Math.max(0,toolbar.current.getBoundingClientRect().bottom)}px`);node.style.setProperty('--dh-panel-bottom',`${Math.max(0,window.innerHeight-statusBar.current.getBoundingClientRect().top)}px`);
   node.style.setProperty('--dh-panel-right',`${Math.max(0,window.innerWidth-shellBox.left-app.clientWidth)}px`);node.style.setProperty('--dh-status-h',`${statusBar.current.offsetHeight}px`);if(!fromScroll)dock();};
  // Without the shell scrolling, the compact panel covers the area (CSS fixes it to the window while the shell scrolls).
  const dock=()=>{const node=host.current,app=shell.current,area=areaNode.current;if(!node||!app||!area)return;const top=Math.max(area.offsetTop,app.scrollTop),bottom=Math.min(area.offsetTop+area.offsetHeight,app.scrollTop+app.clientHeight);node.style.setProperty('--dh-area-top',`${top}px`);node.style.setProperty('--dh-area-height',`${Math.max(0,bottom-top)}px`);};
  // Only the shell moves, by the least amount (the scroll-padding margin included), and only while it overflows.
  const inPanel=(el:Element|null)=>!!el?.closest('#download-activity-panel');
  const focused=()=>{const el=document.activeElement as HTMLElement|null;return el&&shell.current?.contains(el)&&!inPanel(el)?el:null;};
  const shown=(el:HTMLElement|null)=>{const app=shell.current;if(!app||!el)return false;const box=app.getBoundingClientRect(),r=el.getBoundingClientRect(),bar=statusBar.current&&getComputedStyle(statusBar.current).position==='sticky'?statusBar.current.offsetHeight:0;return r.top>=box.top-1&&r.bottom<=box.top+app.clientHeight-bar+1;};
  const reveal=(el:HTMLElement|null)=>{const app=shell.current;if(!app||!el||app.scrollHeight<=app.clientHeight+1)return;const style=getComputedStyle(app),box=app.getBoundingClientRect(),r=el.getBoundingClientRect(),top=box.top+(parseFloat(style.scrollPaddingTop)||0),bottom=box.top+app.clientHeight-(parseFloat(style.scrollPaddingBottom)||0);if(r.top<top)app.scrollTop-=top-r.top;else if(r.bottom>bottom)app.scrollTop+=Math.min(r.bottom-bottom,r.top-top);};
  // Focus that was in view stays in view when the header or area changes size under it (the activity entry appearing grows the header).
  // Scrolling away from it on purpose clears that, so a resize never pulls the reader back.
  let focusShown=false;
  const onFocus=(event:FocusEvent)=>{const el=event.target as HTMLElement;if(!inPanel(el))reveal(el);focusShown=shown(focused());};
  const onScroll=()=>{place(true);focusShown=shown(focused());};
  const onResize=()=>{place();const el=focused();if(focusShown&&el&&!shown(el))reveal(el);focusShown=shown(el);};
  revealer.current=reveal;
  place();const observer=typeof ResizeObserver==='undefined'?null:new ResizeObserver(onResize);for(const el of[toolbar.current,statusBar.current,areaNode.current,shell.current])if(el)observer?.observe(el);window.addEventListener('resize',onResize);const app=shell.current;app?.addEventListener('scroll',onScroll,{passive:true});app?.addEventListener('focusin',onFocus);
  return()=>{observer?.disconnect();window.removeEventListener('resize',onResize);app?.removeEventListener('scroll',onScroll);app?.removeEventListener('focusin',onFocus);};
 },[]);
 const ui=useDownloadActivity(),expanded=!!ui?.expanded,wasExpanded=useRef(expanded);
 // Opening the panel never moves the shell. Closing it puts the shell back where it was opened when focus returns to the invoker, so the
 // returned focus stays visible.
 const openedFrom=useRef<number|null>(null),revealer=useRef<(el:HTMLElement|null)=>void>(()=>{});
 useLayoutEffect(()=>{const node=shell.current;if(node&&expanded&&!wasExpanded.current){openedFrom.current=node.scrollHeight>node.clientHeight+1?node.scrollTop:null;}else if(node&&!expanded&&wasExpanded.current){
  // The panel is nonmodal, so focus may have left it. Focus that was in the panel (now body) goes back to the invoker: restore its scroll.
  // Focus already on another control in the shell stays there and is kept in view instead.
  const active=document.activeElement as HTMLElement|null;if(active&&active!==document.body&&node.contains(active))revealer.current(active);else if(openedFrom.current!==null)node.scrollTop=openedFrom.current;openedFrom.current=null;}wasExpanded.current=expanded;},[expanded]);
 // While the shell scrolls the open panel covers everything, so it is a sheet: everything under it is inert (the skip link, the header
 // apart from the panel's own slot, and the area), so Tab stays in the panel and the status bar. This runs before the activity provider
 // restores focus on close, so the opener is no longer inert by then. Larger sizes keep the nonmodal panel.
 const sheet=scrolling&&expanded;
 useLayoutEffect(()=>{const root=host.current,bar=toolbar.current;if(!sheet||!root||!bar)return;
  const covered=[root.querySelector<HTMLElement>('.dh-skip'),...[...bar.children].flatMap(el=>el.classList.contains('dh-hgroup')?[...el.children].filter(child=>!child.hasAttribute('data-activity-slot')):[el])].filter(Boolean) as HTMLElement[];
  for(const el of covered)el.inert=true;
  return()=>{for(const el of covered)el.inert=false;};
 });
 // The panel's heading is sticky: its height is the panel's top scroll padding, so a control Tab scrolls into view never lands under it.
 useLayoutEffect(()=>{if(!expanded)return;const panel=host.current?.querySelector<HTMLElement>('#download-activity-panel'),head=panel?.querySelector<HTMLElement>('.download-panel-heading');if(!panel||!head)return;
  const pad=()=>{panel.style.scrollPaddingTop=`${head.offsetHeight}px`;};pad();const observer=typeof ResizeObserver==='undefined'?null:new ResizeObserver(pad);observer?.observe(head);
  return()=>{observer?.disconnect();panel.style.scrollPaddingTop='';};
 },[expanded]);
 const summary=ui&&ui.entry&&!ui.expanded?ui.summary:null;
 return <ShellContext.Provider value={{compact:size.compact,statusSlot}}><div ref={host} className={`desktop-home${size.compact?' dh-compact dh-medium':size.medium?' dh-medium':''}${scrolling?' dh-shell-scroll':''}${expanded?' dh-panel-open':''}`} data-area={area}>
  <a className="dh-skip" href="#dh-area">Skip to content</a>
  <div ref={shell} className="dh-app">
   <BuildHeader ref={toolbar} snapshot={snapshot} catalogBuild={catalogBuild} label={label} retention={retention} onChoose={onChoose} areas={areas} area={area}/>
   <main ref={areaNode} id="dh-area" className="dh-area" tabIndex={-1} inert={!!ui?.expanded&&(size.compact||sheet||area==='items'||area==='creatures')}>{children}</main>
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
