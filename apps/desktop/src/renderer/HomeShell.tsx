import {createContext,useContext,useLayoutEffect,useRef,useState,type ReactNode} from 'react';
import type {Build,WorkspaceSnapshot} from '../../../../contracts/generated/discovery';
import {useDownloadActivity} from './DownloadActivity';
import {summaryLine} from './download-activity-state';
import {BuildHeader} from './BuildHeader';
import type {RetentionController} from './useRetention';
import './desktop-home.css';

// Desktop Home shell (docs/design/desktop-home): fixed window, compact build header with the area bar, one visible area, status bar.
// When the header and the area's minimum don't both fit, .dh-app scrolls as a whole (.dh-shell-scroll). The minimum is 10rem, or more
// while a view in the area asks for it (a Tables Data grid needs its pane chrome, its header and three whole rows, D099). The open
// activity panel is then fixed to the window above the status bar's height, within the shell's inner width, and the shell is locked
// while it is open.
// Whatever takes focus in the shell outside the panel, by keyboard or programmatically, is scrolled into the shell's view; the panel
// scrolls its own content.
// Text-relative breakpoints are measured here, as on Discovery, because Chromium does not always re-run rem container queries when only the root text size changes.
// Narrow (#52, D093): below 28rem the Files object rows take two lines, so their local state stays in view.
const MEDIUM_REM=78.4,COMPACT_REM=65.333333,NARROW_REM=28;
// setMinimum(owner, px) asks for at least px of area height while owner shows; null withdraws it. The largest request counts.
type Shell={compact:boolean;statusSlot:HTMLElement|null;setMinimum?:(owner:object,px:number|null)=>void};
export const ShellContext=createContext<Shell>({compact:false,statusSlot:null,setMinimum:()=>{}});
export function useShell(){return useContext(ShellContext);}
// Areas can add their own status bar facts through statusSlot (Tables names its definitions revision and admitted rows).
// The docked activity panel covers the side column or inspector (and the end of the Home pane head), which are inert while it is open.
// In the compact layout the whole area is inert instead. On Items and Creatures the panel covers exactly the detail pane, which
// those views make inert themselves (docs/design/items-view, docs/design/creatures-view).
export function useCoveredSide(){const ui=useDownloadActivity(),{compact}=useShell();return !!ui?.expanded&&!compact;}
export type ShellArea='home'|'tables'|'files'|'items'|'creatures';
export function HomeShell({snapshot,catalogBuild,label,retention,onChoose,area,areas,status,fixtureLabel,children,dialog}:{snapshot:WorkspaceSnapshot;catalogBuild?:Build|null;label:string;retention?:RetentionController;onChoose:()=>void;area:ShellArea;areas:ReactNode;status?:ReactNode;fixtureLabel?:string|null;children:ReactNode;dialog?:ReactNode}){
 const host=useRef<HTMLDivElement>(null),shell=useRef<HTMLDivElement>(null),toolbar=useRef<HTMLElement>(null),statusBar=useRef<HTMLElement>(null),areaNode=useRef<HTMLElement>(null);
 const [size,setSize]=useState<{compact:boolean;medium:boolean;narrow:boolean}>({compact:false,medium:false,narrow:false}),[statusSlot,setStatusSlot]=useState<HTMLElement|null>(null),[scrolling,setScrolling]=useState(false);
 const minimums=useRef(new Map<object,number>()),replace=useRef<()=>void>(()=>{}),rekeep=useRef<()=>void>(()=>{});
 const setMinimum=useRef((owner:object,px:number|null)=>{const map=minimums.current,old=map.get(owner);if(px===null){if(old===undefined)return;map.delete(owner);}else{const next=Math.ceil(px);if(old===next)return;map.set(owner,next);}replace.current();}).current;
 useLayoutEffect(()=>{
  const measure=()=>{const node=host.current;if(!node)return;const root=parseFloat(getComputedStyle(document.documentElement).fontSize)||15,width=node.clientWidth;if(width<=0)return;const next={compact:width<COMPACT_REM*root,medium:width<MEDIUM_REM*root,narrow:width<NARROW_REM*root};setSize(old=>old.compact===next.compact&&old.medium===next.medium&&old.narrow===next.narrow?old:next);};
  measure();
  const resize=typeof ResizeObserver==='undefined'?null:new ResizeObserver(measure);if(host.current)resize?.observe(host.current);
  const text=new MutationObserver(measure);text.observe(document.documentElement,{attributes:true,attributeFilter:['style','class']});window.addEventListener('resize',measure);
  return()=>{resize?.disconnect();text.disconnect();window.removeEventListener('resize',measure);};
 },[]);
 useLayoutEffect(()=>{
  const place=(fromScroll=false)=>{const node=host.current,app=shell.current,area=areaNode.current;if(!node||!app||!area||!toolbar.current||!statusBar.current)return;
   const root=parseFloat(getComputedStyle(document.documentElement).fontSize)||15,shellBox=app.getBoundingClientRect();
   // Measured from the header and the minimum, not the content, so Home taking its natural height can't flip it back.
   const over=toolbar.current.offsetHeight+statusBar.current.offsetHeight+Math.max(10*root,...minimums.current.values())>app.clientHeight+1;setScrolling(old=>old===over?old:over);
   node.style.setProperty('--dh-panel-top',`${Math.max(0,toolbar.current.getBoundingClientRect().bottom)}px`);node.style.setProperty('--dh-panel-bottom',`${Math.max(0,window.innerHeight-statusBar.current.getBoundingClientRect().top)}px`);
   node.style.setProperty('--dh-panel-right',`${Math.max(0,window.innerWidth-shellBox.left-app.clientWidth)}px`);node.style.setProperty('--dh-status-h',`${statusBar.current.offsetHeight}px`);if(!fromScroll)dock();};
  // Without the shell scrolling, the compact panel covers the area (CSS fixes it to the window while the shell scrolls).
  const dock=()=>{const node=host.current,app=shell.current,area=areaNode.current;if(!node||!app||!area)return;const top=Math.max(area.offsetTop,app.scrollTop),bottom=Math.min(area.offsetTop+area.offsetHeight,app.scrollTop+app.clientHeight);node.style.setProperty('--dh-area-top',`${top}px`);node.style.setProperty('--dh-area-height',`${Math.max(0,bottom-top)}px`);};
  // Only the shell moves, by the least amount (the scroll-padding margin included), and only while it overflows.
  const inPanel=(el:Element|null)=>!!el?.closest('#download-activity-panel');
  const focused=()=>{const el=document.activeElement as HTMLElement|null;return el&&shell.current?.contains(el)&&!inPanel(el)?el:null;};
  // What the reader sees of a focused control: a listbox's active option, cut to the inner scrollers that hold it (a Records grid
  // region or the Files list is taller than its scroller), so the shell reveals that part and not the scrolled-away rest (#66). A control
  // scrolled out of its own scroller is not shown, whatever the shell does.
  const box=(el:HTMLElement)=>{const app=shell.current,id=el.getAttribute('aria-activedescendant'),option=id?document.getElementById(id):null,target=option&&el.contains(option)?option:el,r=target.getBoundingClientRect();let top=r.top,bottom=r.bottom;
   // A Records grid row counts from the grid's top, where its column header sticks, so revealing the row brings the column names too
   // (D099; the grid is capped to fit the shell's view).
   const grid=target.closest('.dh-grid-scroll'),row=grid&&!target.closest('thead')?target.closest('tr'):null;if(row){top=Math.min(top,grid!.getBoundingClientRect().top);bottom=Math.max(bottom,row.getBoundingClientRect().bottom);}
   for(let up=target.parentElement;up&&up!==app;up=up.parentElement){if(!/auto|scroll|hidden|clip/.test(getComputedStyle(up).overflowY))continue;const s=up.getBoundingClientRect();top=Math.max(top,s.top);bottom=Math.min(bottom,s.bottom);}
   return bottom>top?{top,bottom,hidden:false}:{top:r.top,bottom:r.bottom,hidden:true};};
  // The shell's bottom scroll padding clears what covers the bottom of its view (the sticky status bar, and the floating activity entry
  // while it shows) plus the same gap as at the top, so that covered band, without the gap, is where a control counts as hidden.
  const shown=(el:HTMLElement|null)=>{const app=shell.current;if(!app||!el)return false;const style=getComputedStyle(app),frame=app.getBoundingClientRect(),r=box(el),covered=Math.max(0,(parseFloat(style.scrollPaddingBottom)||0)-(parseFloat(style.scrollPaddingTop)||0));return !r.hidden&&r.top>=frame.top-1&&r.bottom<=frame.top+app.clientHeight-covered+1;};
  const reveal=(el:HTMLElement|null)=>{const app=shell.current;if(!app||!el||app.scrollHeight<=app.clientHeight+1)return;const style=getComputedStyle(app),frame=app.getBoundingClientRect(),r=box(el),top=frame.top+(parseFloat(style.scrollPaddingTop)||0),bottom=frame.top+app.clientHeight-(parseFloat(style.scrollPaddingBottom)||0);if(r.top<top)app.scrollTop-=top-r.top;else if(r.bottom>bottom)app.scrollTop+=Math.min(r.bottom-bottom,r.top-top);};
  // Focus that was in view stays in view when the header or area changes size under it (the activity entry appearing grows the header).
  // Scrolling away from it on purpose clears that, so a resize never pulls the reader back. A scroll event can also come from layout
  // (the browser clamping the scroll after the area shrank, delivered after content above the focus grew): when the focused control
  // moved within the shell's content or the content height changed, it is handled as a resize, not as the reader scrolling away (#66).
  // Inner scrollers (the Records grid, the lists) are heard in the capture phase: wheeling a grid moves its focused row on purpose, so
  // the shell scroll that wheel chains into next is never taken for a layout shift and never pulls the row back.
  let focusShown=false,focusAt=0,contentHeight=0;
  const at=(el:HTMLElement|null)=>{const app=shell.current;return app&&el?box(el).top-app.getBoundingClientRect().top+app.scrollTop:0;};
  const note=()=>{const el=focused();focusShown=shown(el);focusAt=at(el);contentHeight=shell.current?.scrollHeight??0;};
  const keep=()=>{const el=focused();if(focusShown&&el&&!shown(el))reveal(el);note();};
  // A control just focused is wanted in view even when the shell can't scroll far enough yet (a short pane clamps it), so the resize that
  // follows finishes the reveal.
  // Opening the panel never moves the shell, and closing it restores the shell exactly (#60): focus returning to the invoker keeps the
  // restored scroll even when the invoker is partly out of view (a fallback such as the area heading is revealed as usual).
  // A control focused by a press where it shows is not revealed then: moving it from under the pointer would send the release elsewhere
  // and lose the click. It is still wanted in view, so a later resize (a notice added above it) reveals it (#60).
  // The press lasts from pointerdown to its release (focus moves on pointerdown); a key press ends it too.
  let pressing=false;const onPress=()=>{pressing=true;},onRelease=()=>{pressing=false;};
  // A control the reader activates (a click, Enter or Space) is wanted in view from then on, whatever an earlier scroll did, so the
  // content its action adds above it (a notice) can't push it under the status bar (#60).
  const onActivate=(event:Event)=>{if(event instanceof KeyboardEvent&&event.key!=='Enter'&&event.key!==' ')return;const el=focused();if(el&&el.contains(event.target as Node)){focusShown=true;focusAt=at(el);}};
  const partly=(el:HTMLElement)=>{const app=shell.current;if(!app)return false;const frame=app.getBoundingClientRect(),r=box(el);return !r.hidden&&r.bottom>frame.top&&r.top<frame.top+app.clientHeight;};
  const onFocus=(event:FocusEvent)=>{const el=event.target as HTMLElement,restore=restoreTo.current;restoreTo.current=null;if(restore!==null&&shell.current)shell.current.scrollTop=restore;if(!inPanel(el))lastFocus.current=el;if(!(restore!==null&&el===openedBy.current)&&!inPanel(el)&&!(pressing&&partly(el)))reveal(el);note();if(focused())focusShown=true;};
  const onScroll=(event:Event)=>{if(event.target!==shell.current){const el=focused();if(!el||(event.target as Node).contains(el))note();return;}place(true);const el=focused();if(el&&(Math.abs(at(el)-focusAt)>1||shell.current?.scrollHeight!==contentHeight))keep();else note();};
  const onResize=()=>{place();keep();};
  revealer.current=reveal;replace.current=()=>place();rekeep.current=keep;
  place();const observer=typeof ResizeObserver==='undefined'?null:new ResizeObserver(onResize);for(const el of[toolbar.current,statusBar.current,areaNode.current,shell.current])if(el)observer?.observe(el);window.addEventListener('resize',onResize);const app=shell.current;app?.addEventListener('scroll',onScroll,{passive:true,capture:true});app?.addEventListener('focusin',onFocus);app?.addEventListener('pointerdown',onPress,true);app?.addEventListener('click',onActivate,true);app?.addEventListener('keydown',onActivate,true);for(const type of['pointerup','pointercancel','keydown'])window.addEventListener(type,onRelease,true);
  return()=>{replace.current=()=>{};rekeep.current=()=>{};app?.removeEventListener('pointerdown',onPress,true);app?.removeEventListener('click',onActivate,true);app?.removeEventListener('keydown',onActivate,true);for(const type of['pointerup','pointercancel','keydown'])window.removeEventListener(type,onRelease,true);observer?.disconnect();window.removeEventListener('resize',onResize);app?.removeEventListener('scroll',onScroll,{capture:true});app?.removeEventListener('focusin',onFocus);};
 },[]);
 const ui=useDownloadActivity(),expanded=!!ui?.expanded,wasExpanded=useRef(expanded);
 // The floating entry appearing raises the shell's bottom scroll padding (desktop-home.css): focus that was in view is revealed again
 // above it, so a pager button just revealed under the place where it appears isn't left beneath it (#60).
 const floating=!!ui?.floating;useLayoutEffect(()=>{rekeep.current();},[floating]);
 // Opening the panel never moves the shell. Closing it puts the shell back where it was opened when focus returns to the invoker, so the
 // returned focus stays visible.
 const openedFrom=useRef<number|null>(null),restoreTo=useRef<number|null>(null),openedBy=useRef<HTMLElement|null>(null),lastFocus=useRef<HTMLElement|null>(null),revealer=useRef<(el:HTMLElement|null)=>void>(()=>{});
 useLayoutEffect(()=>{const node=shell.current;if(node&&expanded&&!wasExpanded.current){openedBy.current=lastFocus.current;openedFrom.current=node.scrollHeight>node.clientHeight+1?node.scrollTop:null;}else if(node&&!expanded&&wasExpanded.current){
  // The panel is nonmodal, so focus may have left it. Focus that was in the panel (now body) goes back to the invoker: restore its scroll.
  // Focus already on another control in the shell stays there and is kept in view instead.
  const active=document.activeElement as HTMLElement|null;if(active&&active!==document.body&&node.contains(active))revealer.current(active);else if(openedFrom.current!==null){const top=openedFrom.current;node.scrollTop=top;restoreTo.current=top;requestAnimationFrame(()=>{if(Math.abs(node.scrollTop-top)>1&&document.activeElement===openedBy.current)node.scrollTop=top;if(restoreTo.current===top)restoreTo.current=null;});}openedFrom.current=null;}wasExpanded.current=expanded;},[expanded]);
 // While the shell scrolls the open panel covers everything, so it is a sheet: everything under it is inert (the skip link, the header
 // apart from the panel's own slot, and the area), so Tab stays in the panel and the status bar. This runs before the activity provider
 // restores focus on close, so the opener is no longer inert by then. Larger sizes keep the nonmodal panel.
 // When the layout switches to the sheet with the panel already open (the window or text size changed), focus under it (or on body,
 // where Chromium leaves an element that just became inert) moves into the panel, to its heading (#68).
 const sheet=scrolling&&expanded,sheetBefore=useRef(false),openBefore=useRef(false);
 useLayoutEffect(()=>{const switched=sheet&&!sheetBefore.current&&openBefore.current;sheetBefore.current=sheet;openBefore.current=expanded;
  const root=host.current,bar=toolbar.current;if(!sheet||!root||!bar)return;
  const covered=[root.querySelector<HTMLElement>('.dh-skip'),...[...bar.children].flatMap(el=>el.classList.contains('dh-hgroup')?[...el.children].filter(child=>!child.hasAttribute('data-activity-slot')):[el])].filter(Boolean) as HTMLElement[];
  const active=document.activeElement;
  for(const el of covered)el.inert=true;
  if(switched&&(active===document.body||covered.some(el=>el.contains(active))||!!areaNode.current?.contains(active)))document.querySelector<HTMLElement>('#download-activity-panel #download-surface-heading')?.focus({preventScroll:true});
  return()=>{for(const el of covered)el.inert=false;};
 });
 // The panel's heading is sticky: its height is the panel's top scroll padding, so a control Tab scrolls into view never lands under it.
 // Focus in the panel that was in view stays in view when the panel or its content changes size, as a text-size switch to the sheet
 // does (#74). Scrolling the panel away from it on purpose clears that; a scroll caused by layout (the focused control moved in the
 // panel's content, or the content height changed) doesn't, as in the shell.
 useLayoutEffect(()=>{if(!expanded)return;const panel=host.current?.querySelector<HTMLElement>('#download-activity-panel'),head=panel?.querySelector<HTMLElement>('.download-panel-heading');if(!panel||!head)return;
  const pad=()=>{panel.style.scrollPaddingTop=`${head.offsetHeight}px`;};
  const inside=()=>{const el=document.activeElement as HTMLElement|null;return el&&el!==panel&&panel.contains(el)&&!head.contains(el)?el:null;};
  const view=()=>{const p=panel.getBoundingClientRect();return {top:p.top+head.offsetHeight,bottom:p.top+panel.clientHeight,base:p.top};};
  const shown=(el:HTMLElement)=>{const v=view(),r=el.getBoundingClientRect();return r.top>=v.top-1&&r.bottom<=v.bottom+1;};
  let wanted=false,at=0,height=0;
  const note=()=>{const el=inside();wanted=!!el&&shown(el);at=el?el.getBoundingClientRect().top-view().base+panel.scrollTop:0;height=panel.scrollHeight;};
  const keep=()=>{pad();const el=inside();if(wanted&&el&&!shown(el)){const v=view(),r=el.getBoundingClientRect();if(r.bottom>v.bottom)panel.scrollTop+=Math.min(r.bottom-v.bottom,r.top-v.top);else panel.scrollTop-=v.top-r.top;}note();};
  const onFocus=()=>{note();if(inside())wanted=true;};
  const onScroll=()=>{const el=inside();if(el&&(Math.abs(el.getBoundingClientRect().top-view().base+panel.scrollTop-at)>1||panel.scrollHeight!==height))keep();else note();};
  pad();note();if(inside())wanted=true;
  const observer=typeof ResizeObserver==='undefined'?null:new ResizeObserver(keep);observer?.observe(head);observer?.observe(panel);for(const child of panel.children)observer?.observe(child);
  const text=new MutationObserver(keep);text.observe(document.documentElement,{attributes:true,attributeFilter:['style','class']});
  panel.addEventListener('focusin',onFocus);panel.addEventListener('scroll',onScroll,{passive:true});window.addEventListener('resize',keep);
  return()=>{observer?.disconnect();text.disconnect();panel.removeEventListener('focusin',onFocus);panel.removeEventListener('scroll',onScroll);window.removeEventListener('resize',keep);panel.style.scrollPaddingTop='';};
 },[expanded]);
 const summary=ui&&ui.entry&&!ui.expanded?ui.summary:null;
 return <ShellContext.Provider value={{compact:size.compact,statusSlot,setMinimum}}><div ref={host} className={`desktop-home${size.compact?' dh-compact dh-medium':size.medium?' dh-medium':''}${size.narrow?' dh-narrow':''}${scrolling?' dh-shell-scroll':''}${expanded?' dh-panel-open':''}`} data-area={area}>
  <a className="dh-skip" href="#dh-area">Skip to content</a>
  <div ref={shell} className="dh-app">
   <BuildHeader ref={toolbar} snapshot={snapshot} catalogBuild={catalogBuild} label={label} retention={retention} onChoose={onChoose} areas={areas} area={area}/>
   <main ref={areaNode} id="dh-area" className="dh-area" tabIndex={-1} inert={!!ui?.expanded&&(size.compact||sheet)}>{children}</main>
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
