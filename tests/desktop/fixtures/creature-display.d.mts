import type {CreaturesSnapshot,CreatureSummary,OpeningSourceReference,CreatureDisplayMetadataResult}from '../../../contracts/generated/discovery';
export function creatureDisplay(core:CreaturesSnapshot,summary:CreatureSummary,slot:number,opening:OpeningSourceReference,state?:CreatureDisplayMetadataResult['state']):CreatureDisplayMetadataResult;
import type {CreatureDisplayTarget,TableDefinitionSnapshot}from '../../../contracts/generated/discovery';
export function displayDefinition(target:CreatureDisplayTarget,buildVersion:string):Promise<TableDefinitionSnapshot>;
