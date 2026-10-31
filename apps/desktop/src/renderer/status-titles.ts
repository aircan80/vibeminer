import {useEffect,type RefObject} from 'react';

// Whether a fact's text is wider than its box, to a fraction of a pixel: the ellipsis shows for any cut, and scrollWidth rounds.
// Inline facts inside a fact have no box of their own to cut.
export function isCut(el:HTMLElement){
 const cs=getComputedStyle(el);if(cs.display==='inline'||cs.display==='contents'||!el.getClientRects().length)return false;
 const range=document.createRange();range.selectNodeContents(el);const text=range.getBoundingClientRect().width;
 const box=el.getBoundingClientRect().width-parseFloat(cs.paddingLeft)-parseFloat(cs.paddingRight)-parseFloat(cs.borderLeftWidth)-parseFloat(cs.borderRightWidth);
 return text>box+.1;
}
// A status bar is one line, so a fact that doesn't fit ends in an ellipsis (#134). A cut fact gets its whole text as a tooltip
// and, while any of it shows, a tab stop: focusing it shows the whole text on a line above the bar (.vm-status-full, styles.css),
// so it can be read without a pointer. A fact that fits has neither. The bar's facts change with the area and the activity, and its width
// with the window, the text size and the font, so this re-checks on any of those. Only what is set here is removed again, and
// a focused fact keeps its tab stop until it loses focus.
export function useTruncationTitles(ref:RefObject<HTMLElement|null>){
 useEffect(()=>{
  const bar=ref.current;if(!bar)return;
  let frame=0,full:HTMLDivElement|null=null;
  const hide=()=>{full?.remove();full=null;};
  const show=(el:HTMLElement)=>{
   if(!el.isConnected||!('cutTitle' in el.dataset)){hide();return;}
   if(!full){full=document.createElement('div');full.className='vm-status-full';full.setAttribute('aria-hidden','true');}
   const host=bar.parentElement??document.body;if(full.parentElement!==host)host.append(full);
   const r=el.getBoundingClientRect(),b=bar.getBoundingClientRect(),cs=getComputedStyle(bar);
   full.textContent=el.title;full.style.fontSize=cs.fontSize;full.style.bottom=`${Math.max(0,innerHeight-b.top)+4}px`;
   full.style.left=`${Math.max(4,Math.min(r.left,innerWidth-4-full.offsetWidth))}px`;
  };
  const check=()=>{frame=0;for(const el of bar.querySelectorAll<HTMLElement>('span')){
   const cut=isCut(el),text=(el.textContent??'').replace(/\s+/g,' ').trim();
   // A fact squeezed to nothing shows nothing to focus, so it gets the tooltip but no tab stop.
   const shown=el.getBoundingClientRect().width>.5,focused=document.activeElement===el;
   if(cut&&text){if(el.title!==text)el.title=text;el.dataset.cutTitle='';if(shown)el.tabIndex=0;else if(!focused)el.removeAttribute('tabindex');}
   else if('cutTitle' in el.dataset&&!focused){el.removeAttribute('title');el.removeAttribute('tabindex');delete el.dataset.cutTitle;}
   resize?.observe(el);
  }
  // The focused fact can go (the busy line ends) without a focusout reaching the bar; its line goes with it.
  const active=document.activeElement;if(active instanceof HTMLElement&&bar.contains(active)){if(full)show(active);}else hide();};
  const soon=()=>{if(!frame)frame=requestAnimationFrame(check);};
  const onFocus=(e:FocusEvent)=>{if(e.target instanceof HTMLElement)show(e.target);};
  const onBlur=()=>{hide();soon();};
  const resize=typeof ResizeObserver==='undefined'?null:new ResizeObserver(soon);resize?.observe(bar);
  const content=new MutationObserver(soon);content.observe(bar,{childList:true,subtree:true,characterData:true});
  const root=new MutationObserver(soon);root.observe(document.documentElement,{attributes:true,attributeFilter:['style','class']});
  bar.addEventListener('focusin',onFocus);bar.addEventListener('focusout',onBlur);
  window.addEventListener('resize',soon);document.fonts?.addEventListener?.('loadingdone',soon);
  check();
  return()=>{if(frame)cancelAnimationFrame(frame);hide();resize?.disconnect();content.disconnect();root.disconnect();bar.removeEventListener('focusin',onFocus);bar.removeEventListener('focusout',onBlur);window.removeEventListener('resize',soon);document.fonts?.removeEventListener?.('loadingdone',soon);};
 },[ref]);
}
