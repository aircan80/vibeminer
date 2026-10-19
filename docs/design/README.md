# VibeMiner UI design references

The first screen follows **[desktop discovery](desktop-discovery/index.html)** ([D059](../DECISIONS.md)). It replaced the layout of **guided build discovery** ([`build-discovery/index.html?concept=guided&state=ready&product=wow`](build-discovery/index.html?concept=guided&state=ready&product=wow)), whose meaning it keeps. The repository keeps that exact HTML and its local navigation dependencies. The other concepts in its selector are historical alternatives. The older standalone `mockups/atlas` prototype is superseded and is not a reference.

Cross-screen rules (tokens, states, progress, focus, verification) are in [UI guidelines](../UI-GUIDELINES.md).

## Reference views

| View | Reference | Role |
| --- | --- | --- |
| Desktop discovery | [`desktop-discovery/index.html`](desktop-discovery/index.html) (states via `?state=`; see its [README](desktop-discovery/README.md)) | Selected first screen |
| Guided discovery | [`concept=guided&state=ready&product=wow`](build-discovery/index.html?concept=guided&state=ready&product=wow) | Superseded layout; its discovery contract still applies |
| Preparation | [`option=dedicated&state=indexUnknown`](build-preparation/index.html?option=dedicated&state=indexUnknown) | Selected in Java; carried forward |
| C build overview (Home) | [`concept=archive&state=ready`](build-mining-home/index.html?concept=archive&state=ready) | Selected in Java; discovery's destination |
| Home and Tables with area navigation | [Home](top-navigation/home.html), [Tables](top-navigation/tables.html) | Selected horizontal shell and Tables workbench |
| Downloads and activity | [Option A: side panel](download-activity/index.html?concept=drawer&state=active&panel=open) | Option A selected as the default; B kept for comparison |

The HTML files can contain older alternatives. Only the views above guide the app. Their selectors, demo controls, counters, product aliases and quantities are authored examples, not production controls or facts.

## Horizontal navigation and Tables

- Area navigation is horizontal under the build header. There is no global sidebar.
- Tables uses the Explorer layout: catalogue on the left, then the center, then a 340px inspector.
- Prototype rows, controls and coverage figures are examples. What production shows is defined in [tables-v1](../../contracts/tables-v1.md) and [table-data-v1](../../contracts/table-data-v1.md).
- [Table research](tables/RESEARCH.md) records the definition sources. The historical Java [Tables stories](../migration/java-tables/TABLES-STORIES.md) and [relationship-label rules](../migration/java-tables/TABLE-RELATION-LABELS.md) keep the larger phased scope.

## Discovery contract

- Product goes immediately under the discovery question, before results. Show the exact product ID and a reviewed description.
- Choosing a product refreshes metadata. Selecting a row doesn't download payloads or open the build.
- All / Local / Currently advertised share one list. Publication, local evidence and remote availability stay separate facts.
- Group regions only when every identity-bearing reference agrees. Keep the original receipts, and show the chosen acquisition route separately.
- There's one primary Open Build action, with bounded automatic preparation and explicit failure/retry.
- Keep the exact selection through async work. Stale results can't navigate to another context.
- Show readiness separately from activity and transfer progress.

## Preparation and overview

- Use the dedicated preparation screen for real configuration/DL acquisition, verification and download-size preparation. The [size-first flow](../SIZE-AND-DEMAND-ARCHITECTURE.md) means Home doesn't wait for full research-catalog indexing.
- Healthy saved artifacts skip the screen after revalidation.
- Back detaches without cancelling. Explicit Cancel shows Stopping until cleanup completes.
- Show numbered actual stages with short explanations. Keep manifest encoded size, dated local credit and unprepared research data distinct.
- The C overview keeps exact product/build/locale labels, plain readiness, a separate Retained measurement and the On demand / Download all policy. The mode control changes real retention policy. Fixture percentages never stand in for current evidence. See [product](../PRODUCT.md), [async work](../ASYNC-AND-JOBS.md) and [download activity](download-activity/README.md).

## Fidelity

- Carry the selected mockups' HTML structure, fonts, sizes, weights and colours into React faithfully ([D021](../DECISIONS.md)).
- Discovery follows the desktop reference: toolbar, build listbox, pinned-action inspector and status bar in a window that never scrolls.
- Fix real accessibility or usability defects on purpose and record them. Don't drift silently.
- Record future redesigns as new design references instead of editing the imported source. Then compare the implementation against the new reference.
- Compare real Electron captures with the reference at 1280×800 and 1440×1100, 100% and 200% text. A copied HTML file is a reference asset, not implemented UI.
- Material visual or component decisions get UI review. Workflow, wording, navigation and recovery get usability review. Data meaning behind status, scope and research controls gets WoW datamining review. These reviews don't add an approval step for routine work on agreed behaviour.

## Source provenance

[The import manifest](../migration/java-reference.json) records original paths, the source commit and SHA-256 values. Keep imported HTML unchanged until a design revision is recorded. Production UI lives under `apps/desktop/`. The mockups do no real network, parsing or persistence.
