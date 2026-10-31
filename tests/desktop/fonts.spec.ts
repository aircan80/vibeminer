import { test, expect } from '@playwright/test';
import { launch } from './helpers';
import { systemFonts } from './fonts';

// e2e renders in DejaVu on every machine, as on the CI runner (#138): the app's generic families resolve to the
// dejavu-fonts-ttf fonts, with Ubuntu's slight hinting. Hinting changes widths: this line is 766.640625px regular and
// 860.125px bold at 15px with slight hinting, and 778px and 851px with fontconfig's default full hinting.
const dejaVu = { line: 'Object 2879941 Verified locally 12.1.5.70448 Current Encoded size Local at assessment Memberships', regular: 766.640625, bold: 860.125 };
test('system-ui, sans-serif, ui-monospace and monospace render in DejaVu inside the e2e app', async () => {
  test.skip(systemFonts, 'VIBEMINER_E2E_SYSTEM_FONTS=1 keeps the machine\'s fonts');
  expect(process.env.FONTCONFIG_FILE).toMatch(/artifacts[\\/]e2e-fonts[\\/]fonts\.conf$/);
  const session = await launch(true);
  try {
    const { page } = session;
    await expect(page.getByRole('heading', { name: 'Builds', level: 1 })).toBeVisible({ timeout: 20_000 });
    const probes = [
      { family: 'system-ui', weight: 400, font: 'DejaVuSans' }, { family: 'system-ui', weight: 600, font: 'DejaVuSans-Bold' },
      { family: 'sans-serif', weight: 400, font: 'DejaVuSans' }, { family: 'ui-monospace', weight: 400, font: 'DejaVuSansMono' },
      { family: 'monospace', weight: 400, font: 'DejaVuSansMono' }, { family: 'ui-monospace', weight: 700, font: 'DejaVuSansMono-Bold' },
    ];
    const widths = await page.evaluate(({ probes, line }) => {
      probes.forEach((p, i) => {
        const span = document.createElement('span'); span.id = `font-probe-${i}`; span.textContent = line;
        span.style.cssText = `position:absolute;white-space:nowrap;font:${p.weight} 15px ${p.family}`; document.body.append(span);
      });
      return { regular: document.getElementById('font-probe-0')!.getBoundingClientRect().width, bold: document.getElementById('font-probe-1')!.getBoundingClientRect().width };
    }, { probes, line: dejaVu.line });
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('DOM.enable'); await cdp.send('CSS.enable');
    const { root } = await cdp.send('DOM.getDocument');
    const used = async (selector: string) => {
      const { nodeId } = await cdp.send('DOM.querySelector', { nodeId: root.nodeId, selector });
      return (await cdp.send('CSS.getPlatformFontsForNode', { nodeId })).fonts.map(f => f.postScriptName);
    };
    for (const [i, p] of probes.entries()) expect(await used(`#font-probe-${i}`), `${p.family} ${p.weight}`).toEqual([p.font]);
    // The app's own text, too, not just the families named here.
    expect(await used('h1'), 'the Builds heading').toEqual(['DejaVuSans-Bold']);
    // Slight hinting, as on Ubuntu, not fontconfig's full-hinting default.
    expect(Math.abs(widths.regular - dejaVu.regular), `regular ${widths.regular}px`).toBeLessThanOrEqual(.5);
    expect(Math.abs(widths.bold - dejaVu.bold), `bold ${widths.bold}px`).toBeLessThanOrEqual(.5);
  } finally { await session.close(); }
});
