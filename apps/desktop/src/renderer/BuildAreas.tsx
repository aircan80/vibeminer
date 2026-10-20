import { useLayoutEffect, useRef, type RefObject } from 'react';
export type BuildArea = 'overview' | 'tables' | 'files' | 'items' | 'creatures';
// Home, then the raw source layer (Tables, Files), then VibeMiner's joined views (Items, Creatures).
// Items and Creatures are omitted, with their separator, when this engine or build can't provide them.
export function BuildAreas({ active, onHome, onTables, onFiles, onItems, onCreatures }: { active: BuildArea; onHome: () => void; onTables: () => void; onFiles: () => void; onItems?: () => void; onCreatures?: () => void }) {
  const area = (id: BuildArea, label: string, go: () => void) => <button aria-current={active === id ? 'page' : undefined} onClick={go}>{label}</button>;
  return <nav className="dh-areas" aria-label="Build areas">
    {area('overview', 'Home', onHome)}
    <span className="dh-area-group" role="group" aria-label="Source data">{area('tables', 'Tables', onTables)}{area('files', 'Files', onFiles)}</span>
    {(onItems || onCreatures) && <><span className="dh-area-sep" aria-hidden="true"/><span className="dh-area-group" role="group" aria-label="Game data">{onItems && area('items', 'Items', onItems)}{onCreatures && area('creatures', 'Creatures', onCreatures)}</span></>}
  </nav>;
}

// Focus follows the committed visible area; a local return restores its own origin and the Home pane's scroll position.
export function useHomeFocus(pane: string, heading: RefObject<HTMLHeadingElement | null>, origin: { current: { element: HTMLElement | null; scroll: number } }, show: () => void, scroller?: RefObject<HTMLElement | null>) {
  const intent = useRef<'heading' | 'origin' | null>(null);
  function apply(kind: 'heading' | 'origin') {
    if (kind === 'origin' && origin.current.element?.isConnected && !origin.current.element.closest('[hidden]')) { if (scroller?.current) scroller.current.scrollTop = origin.current.scroll; origin.current.element.focus({ preventScroll: true }); }
    else { if (scroller?.current) scroller.current.scrollTop = 0; heading.current?.focus({ preventScroll: true }); }
  }
  useLayoutEffect(() => { if (pane === 'overview' && intent.current) { const next = intent.current; intent.current = null; apply(next); } }, [pane]);
  function enter(kind: 'heading' | 'origin') { if (pane === 'overview') apply(kind); else { intent.current = kind; show(); } }
  return { home: () => enter('heading'), returnHome: () => enter('origin') };
}
