import type {ItemsSnapshot,ItemClassificationBatch,TableDataSnapshot,TableDefinitionSnapshot,TableDataRow} from '../../../contracts/generated/discovery';
import type {ClassificationParams} from '../../../apps/desktop/src/shared/classifications';
export function classificationSource(items:ItemsSnapshot,kind:'class'|'subclass',scenario?:string):TableDataSnapshot;
export function classificationBatch(items:ItemsSnapshot,p:ClassificationParams,scenario?:string):ItemClassificationBatch;
export function classificationDefinition(items:ItemsSnapshot,kind:'class'|'subclass',version:string,scenario?:string):TableDefinitionSnapshot;
export function classificationRows(items:ItemsSnapshot,kind:'class'|'subclass',id:string,scenario?:string):TableDataRow;
