import { act, cleanup, render, screen } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { useLayoutEffect, useState, type KeyboardEvent } from 'react';
import { afterEach, expect, it, vi } from 'vitest';
import { useListbox } from '../src/renderer/FilesCatalog';

afterEach(cleanup);
// A key pressed after a page arrives but before React runs that render's passive effects must keep its selection.
// The layout effect stands in for that key press: it runs after commit and before the passive effects.
function Harness({ entries }: { entries: string[] }) {
  const list = useListbox<string>(entries, entry => entry);
  const [pressed, setPressed] = useState(false);
  useLayoutEffect(() => { if (entries.length && !pressed) { setPressed(true); list.props.onKeyDown({ key: 'ArrowDown', preventDefault() {} } as KeyboardEvent<HTMLDivElement>); } }, [entries]);
  return <div {...list.props} aria-label="Authored list">{entries.map((entry, i) => <div key={entry} {...list.option(i)}>{entry}</div>)}<output>{list.selected?.entry ?? 'none'}</output></div>;
}
it('keeps a key press that lands before the new page\'s effects run', () => {
  Element.prototype.scrollIntoView = vi.fn();
  const view = render(<Harness entries={[]}/>);
  act(() => { view.rerender(<Harness entries={['a', 'b', 'c']}/>); });
  expect(screen.getByRole('status')).toHaveTextContent('b');
  expect(screen.getByRole('option', { name: 'b' })).toHaveAttribute('aria-selected', 'true');
  expect(screen.getByRole('listbox')).toHaveAttribute('aria-activedescendant', screen.getByRole('option', { name: 'b' }).id);
});
