import type {CreaturesContext,CreaturesSnapshot,CreatureSummary,TableDataRow,TableDefinitionSnapshot,CreaturesPage,CreatureInspectResult} from '../../../contracts/generated/discovery';
export function creaturesSnapshot(context?:CreaturesContext,partial?:boolean):CreaturesSnapshot;
export function creatureRow(s:CreaturesSnapshot,id:string):TableDataRow|null;
export function creatureSummary(s:CreaturesSnapshot,id:string):CreatureSummary|null;
export function creaturesPage(s:CreaturesSnapshot,p:Record<string,unknown>):CreaturesPage;
export function creatureInspect(s:CreaturesSnapshot,p:{creatureId:string}):CreatureInspectResult;

export function creatureDefinition(s:CreaturesSnapshot,buildVersion:string):TableDefinitionSnapshot;
export const creatureTypeText:Record<string,string>,creatureFamilyText:Record<string,string>;
