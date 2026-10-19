import type {ItemsSnapshot,ItemsContext,ItemSummary,ItemDetail,ItemsPage} from '../../../contracts/generated/discovery';
import type {ItemsPageParams} from '../../../apps/desktop/src/shared/items';
export function itemsSnapshot(context?:ItemsContext):ItemsSnapshot;
export function itemSummary(snapshot:ItemsSnapshot,id:string,withIcons?:boolean):ItemSummary|null;
export function itemDetail(snapshot:ItemsSnapshot,id:string,withIcons?:boolean):ItemDetail|null;
export function itemsPage(snapshot:ItemsSnapshot,params:ItemsPageParams,withIcons?:boolean):ItemsPage;
export function itemsDefinition(snapshot:ItemsSnapshot,tableId:string,buildVersion:string):import('../../../contracts/generated/discovery').TableDefinitionSnapshot;
export function itemRows(snapshot:ItemsSnapshot,id:string,withIcons?:boolean):{itemRow:import('../../../contracts/generated/discovery').TableDataRow;sparseRow:import('../../../contracts/generated/discovery').TableDataRow|null}|null;
