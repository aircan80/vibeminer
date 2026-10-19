import { useLayoutEffect, useRef, type RefObject } from 'react';
export type BuildArea = 'overview' | 'tables' | 'objects' | 'files' | 'items' | 'creatures';
export function BuildAreas({ active, onHome, onTables, onItems, onCreatures, onObjects, objectsAvailable, onFiles }: { active: BuildArea; onHome: () => void; onTables: () => void; onItems?: () => void; onCreatures?:()=>void; onObjects?: () => void; objectsAvailable?: boolean; onFiles?: () => void }) {
  return <nav className="area-nav" aria-label="Build areas">
    <button aria-current={active === 'overview' ? 'page' : undefined} onClick={onHome}>Home</button>
    <button aria-current={active === 'tables' ? 'page' : undefined} onClick={onTables}>Tables</button>
    {onItems && <button aria-current={active === 'items' ? 'page' : undefined} onClick={onItems}>Items</button>}
    {onCreatures && <button aria-current={active==='creatures'?'page':undefined} onClick={onCreatures}>Creatures</button>}
    {onObjects && objectsAvailable && <button aria-current={active === 'objects' ? 'page' : undefined} onClick={onObjects}>Download objects</button>}
    {onFiles ? <button aria-current={active === 'files' ? 'page' : undefined} onClick={onFiles}>Files</button> : null}
  </nav>;
}

// Focus follows the committed visible area; a local return restores its own origin.
export function useHomeFocus(pane: string, heading: RefObject<HTMLHeadingElement | null>, origin: { current: { element: HTMLElement | null; scroll: number } }, show: () => void) {
  const intent = useRef<'heading' | 'origin' | null>(null);
  function apply(kind: 'heading' | 'origin') {
    if (kind === 'origin' && origin.current.element?.isConnected && !origin.current.element.closest('[hidden]')) { origin.current.element.focus({ preventScroll: true }); window.scrollTo(0, origin.current.scroll); }
    else { heading.current?.focus({ preventScroll: true }); window.scrollTo(0, 0); }
  }
  useLayoutEffect(() => { if (pane === 'overview' && intent.current) { const next = intent.current; intent.current = null; apply(next); } }, [pane]);
  function enter(kind: 'heading' | 'origin') { if (pane === 'overview') apply(kind); else { intent.current = kind; show(); } }
  return { home: () => enter('heading'), returnHome: () => enter('origin') };
}
