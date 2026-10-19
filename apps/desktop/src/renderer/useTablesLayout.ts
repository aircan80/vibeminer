import {useLayoutEffect,type RefObject} from 'react';
// Preserve the source's 1100/700px breakpoints at15px, including dynamic text scaling.
// Chromium can retain a stale font-relative container query after a hidden view opens.
export function useTablesLayout(element:RefObject<HTMLElement|null>,active:boolean){
 useLayoutEffect(()=>{
  const node=element.current;if(!node||!active)return;
  const measure=()=>{const rootFont=Number.parseFloat(getComputedStyle(document.documentElement).fontSize)||15,width=node.getBoundingClientRect().width;if(width<=0)return;const next=width<=rootFont*(700/15)?'compact':width<=rootFont*(1100/15)?'medium':'wide';if(node.dataset.layout!==next)node.dataset.layout=next;};
  measure();const resize=typeof ResizeObserver==='undefined'?null:new ResizeObserver(measure);resize?.observe(node);
  const text=new MutationObserver(measure);text.observe(document.documentElement,{attributes:true,attributeFilter:['style','class']});window.addEventListener('resize',measure);
  return()=>{resize?.disconnect();text.disconnect();window.removeEventListener('resize',measure);};
 },[active]);
}
