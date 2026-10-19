import { test, expect } from '@playwright/test';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { launch, root } from './helpers';

test('preparation numbers actual stages, preserves them on reattach, and retires them on cancel', async () => {
  const session = await launch(true);
  try {
    const { page, app } = session;
    const captures = path.join(root, 'artifacts/stage-progress');
    await mkdir(captures, { recursive: true });
    await writeFile(path.join(session.data, 'hold-open-cancel'), 'hold');
    await page.locator('.build-row').first().click();
    await page.getByRole('button', { name: 'Open Build', exact: true }).click();
    const phase = page.locator('.preparation-view .phase');
    await expect(phase).toContainText('[1/…] Checking saved configuration');
    await expect(phase).toContainText('Check the exact saved source and configuration documents. The stage count is determined after the configuration check.');
    await expect(page.getByRole('heading', { name: 'Open this build overview' })).toBeFocused();
    const base = await page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).fontSize));
    await page.screenshot({ path: path.join(captures, 'preparation-bootstrap.png'), fullPage: true });
    await writeFile(path.join(session.data, 'advance-open-step'), 'advance');
    await expect(phase).toContainText('[3/5] Building download ledger');
    await expect(phase.locator('.phase-description')).toHaveText('Deduplicate full encoding keys and their published encoded sizes.');
    await expect(phase).not.toContainText('stage count is determined');
    await expect(page.locator('.preparation-view .progress')).toHaveCount(0);
    await page.locator('.download-activity-trigger:visible').click();
    const task = page.locator('#download-activity-panel article[data-activity-key^="workspace:"]');
    await expect(task.getByText('2 of 6 objects · 33% of current stage', {exact: true})).toBeVisible();
    await expect(phase).toHaveAttribute('aria-live', 'off');
    await expect(task.getByRole('status')).toHaveText('Building download ledger · 2 of 6 objects');
    await page.keyboard.press('Escape');
    await expect(page.locator('.stage-announcement')).toHaveAttribute('aria-live', 'polite');
    await page.getByRole('button', { name: /Back to build list/ }).click();
    await expect(page.getByRole('button', { name: 'Open Build', exact: true })).toBeFocused();
    await page.getByRole('button', { name: 'Open Build', exact: true }).click();
    await expect(phase).toContainText('[3/5] Building download ledger');
    const calls = (await readFile(path.join(session.data, 'requests.ndjson'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    expect(calls.filter(call => call.method === 'workspace.open')).toHaveLength(1);
    for (const [width, height] of [[1280, 800], [1440, 1100]]) {
      await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setContentSize(size.width, size.height), { width, height });
      for (const scale of [1, 2]) {
        await page.evaluate(font => { document.documentElement.style.fontSize = `${font}px`; }, base * scale);
        const layout = await phase.evaluate(element => {
          const phase = element.getBoundingClientRect();
          const description = element.querySelector('.phase-description')!.getBoundingClientRect();
          return { overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
            explanationBelow: description.top > phase.top,
            explanationWithin: description.left >= phase.left && description.right <= phase.right + 1,
            wholeExplanationVisible: description.bottom <= phase.bottom + 1 };
        });
        expect(layout, `${width}x${height} ${scale * 100}%`).toEqual({ overflow: false, explanationBelow: true, explanationWithin: true, wholeExplanationVisible: true });
        await expect(page.locator('.preparation-view').getByText('Authored test data', { exact: true })).toBeVisible();
        await page.screenshot({ path: path.join(captures, `preparation-stage-${width}x${height}-${scale * 100}.png`), fullPage: true });
      }
    }
    await page.getByRole('button', { name: 'Cancel preparation', exact: true }).click();
    await expect(phase).toContainText('Stopping preparation…');
    await expect(phase.locator('.phase-count')).toHaveCount(0);
    await expect(phase).not.toContainText('Deduplicate full encoding keys');
    await writeFile(path.join(session.data, 'retire-open'), 'retire');
    await expect(phase).toContainText('Preparation canceled');
    await expect(phase.locator('.phase-count')).toHaveCount(0);
  } finally { await session.close(); }
});
