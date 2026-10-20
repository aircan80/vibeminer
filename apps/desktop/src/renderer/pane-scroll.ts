// In the desktop Home shell the window never scrolls; each area pane (.dh-area-scroll) does.
// These helpers save and restore the pane that holds an element, and fall back to the window elsewhere.
export const paneScroller = (element: Element | null | undefined) => element?.closest<HTMLElement>('.dh-area-scroll') ?? null;
export function paneScrollTop(element: Element | null | undefined) { const pane = paneScroller(element); return pane ? pane.scrollTop : window.scrollY; }
export function restorePaneScroll(element: Element | null | undefined, top: number) { const pane = paneScroller(element); if (pane) pane.scrollTop = top; else window.scrollTo(0, top); }
