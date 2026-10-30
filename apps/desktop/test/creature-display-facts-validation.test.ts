import {it,expect} from 'vitest';
import {creaturesSnapshot,creatureSummary} from '../../../tests/desktop/fixtures/creatures.mjs';
import {creatureDisplay,creatureDisplayFacts} from '../../../tests/desktop/fixtures/creature-display.mjs';
import workspace from '../../../tests/desktop/fixtures/download-workspace.json';
import labels from '../../../contracts/examples/creature-labels.json';
import example from '../../../contracts/examples/creature-display-facts.json';
import type {CreatureDisplayFactsResult} from '../../../contracts/generated/discovery';
import {creatureDisplayFactsMatches,creatureLabelListsMatches} from '../src/shared/creature-display';
// creature-display-facts-v1 (#62): each cell names the exact target column, keys come in fixed order, and the edge is the same cell.
const core=creaturesSnapshot({productId:workspace.build.productId,buildIdentity:workspace.build.id,sourceRegion:workspace.sourceReference.region,researchLocale:'enUS'}),summary=creatureSummary(core,'0')!,opening=workspace.sourceReference,p={...core.context,artifactId:core.artifactId,creatureId:summary.id,slot:0},authority={core,summary,opening};
const facts=(state:Parameters<typeof creatureDisplay>[4]='metadataResolved')=>creatureDisplayFacts(creatureDisplay(core,summary,0,opening,state)) as CreatureDisplayFactsResult;
it('admits the facts of a resolved chain and of a chain with only a display row',()=>{
 expect(creatureDisplayFactsMatches(facts(),p,authority)).toBe(true);
 const display=facts('noDeclaredModel');expect(display.records.map(r=>r.role)).toEqual(['display']);expect(creatureDisplayFactsMatches(display,p,authority)).toBe(true);
 const none=facts('displayAmbiguous');expect(none.records).toEqual([]);expect(creatureDisplayFactsMatches(none,p,authority)).toBe(true);
});
it('refuses a cell that names another column, a reordered or missing key, a record without a row and an edge that differs',()=>{
 for(const mutate of[
  (x:CreatureDisplayFactsResult)=>{x.records[0]!.facts[1].cells[0]!.column='SizeClass';},
  (x:CreatureDisplayFactsResult)=>{x.records[0]!.facts[1].cells[0]!.columnIndex=99;},
  (x:CreatureDisplayFactsResult)=>{x.records[0]!.facts.reverse();},
  (x:CreatureDisplayFactsResult)=>{(x.records[0]!.facts as unknown[]).pop();},
  (x:CreatureDisplayFactsResult)=>{x.records[0]!.facts[0].cells[0]!.value='20';},
  (x:CreatureDisplayFactsResult)=>{x.records[0]!.recordId='101';},
  (x:CreatureDisplayFactsResult)=>{x.records[1]!.tableId='bfdaf9f1';},
  (x:CreatureDisplayFactsResult)=>{x.records[0]!.facts[1]={key:'scale',state:'unsupported',cells:[]};},
  (x:CreatureDisplayFactsResult)=>{x.records[0]!.raw.push({...x.records[0]!.raw[0]});},
  (x:CreatureDisplayFactsResult)=>{x.factsRevision='creature-display-facts-v2' as never;},
  (x:CreatureDisplayFactsResult)=>{x.metadata.slot=1;},
 ]){const v=facts();mutate(v);expect(creatureDisplayFactsMatches(v,p,authority)).toBe(false);}
 const extra=facts('noDeclaredModel');(extra.records as unknown[]).push(structuredClone(facts().records[1]!));expect(creatureDisplayFactsMatches(extra,p,authority)).toBe(false);
});
it('accepts an unsupported fact only when the layout has no such column',()=>{
 const v=facts();v.metadata.sources[1]!.snapshot.columns.splice(1,1,{name:'Other',family:'float',isId:false,reference:null});
 v.records[1]!.facts[1]={key:'scale',state:'unsupported',cells:[]};expect(creatureDisplayFactsMatches(v,p,authority)).toBe(true);
});
it('checks the label list and the authored example shape',()=>{
 expect(creatureLabelListsMatches(labels as never)).toBe(true);
 expect(creatureLabelListsMatches({...labels,classification:[...labels.classification].reverse()} as never)).toBe(false);
 expect(creatureLabelListsMatches({...labels,labelRevision:'x'} as never)).toBe(false);
 expect(example.records.map(r=>r.facts.map(f=>f.key))).toEqual([['model','scale','textureVariations'],['file','scale']]);
});
