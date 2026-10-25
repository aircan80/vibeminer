import {useState} from 'react';
import {cleanup,fireEvent,render,screen,waitFor} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import {afterEach,it,expect,vi} from 'vitest';
import type {DiscoveryBridge} from '../src/shared/bridge';
import {ItemsBrowser} from '../src/renderer/ItemsBrowser';
import {TablesExplorer} from '../src/renderer/TablesExplorer';
import {useItemsLoading} from '../src/renderer/useItemsLoading';
import {useTableLoading} from '../src/renderer/useTableLoading';
import {useItemSourceNavigation} from '../src/renderer/useItemSourceNavigation';
import {itemsSnapshot,itemsPage,itemDetail,itemRows,itemsDefinition,itemFacts,itemLabelLists} from '../../../tests/desktop/fixtures/items.mjs';
import workspace from '../../../tests/desktop/fixtures/download-workspace.json';
afterEach(cleanup);
it('opens the exact admitted source artifact/record and returns to the preserved Items query and invoking action',async()=>{
 vi.spyOn(window,'scrollTo').mockImplementation(()=>{});Element.prototype.scrollIntoView=vi.fn();const s=itemsSnapshot(),definition=itemsDefinition(s,'50238ec2',workspace.build.version);
 const bridge={onProgress:()=>()=>{},itemsData:vi.fn(async()=>({ok:true,value:{snapshot:s,dependencies:[{tableId:'50238ec2',state:'ready',artifactId:s.itemSource.artifactId},{tableId:'919be54e',state:'ready',artifactId:s.sparseSource.artifactId}]}})),itemsPage:vi.fn(async p=>({ok:true,value:itemsPage(s,p)})),itemInspect:vi.fn(async p=>({ok:true,value:{context:s.context,artifactId:s.artifactId,itemId:p.itemId,detail:itemDetail(s,p.itemId)}})),tableDefinition:vi.fn(async p=>({ok:true,value:itemsDefinition(s,p.tableId,workspace.build.version)})),tablesCatalog:vi.fn(async p=>({ok:true,value:{...s.context,provider:definition.provider,query:p.query??'',entries:[definition.entry],totalCount:'1',nextCursor:null}})),tableRows:vi.fn(async p=>({ok:true,value:{...p,rows:[itemRows(s,p.recordId)![p.tableId==='50238ec2'?'itemRow':'sparseRow']],totalCount:'1',nextCursor:null}})),tablePrepare:vi.fn(),tableData:vi.fn(),itemsPrepare:vi.fn(),itemFacts:vi.fn(async p=>({ok:true,value:itemFacts(s,p)})),itemLabels:vi.fn(async()=>({ok:true,value:itemLabelLists}))}as unknown as DiscoveryBridge;
 function Harness(){const[area,setArea]=useState('items'),items=useItemsLoading(bridge),tables=useTableLoading(bridge),route=useItemSourceNavigation(area,setArea);return <><ItemsBrowser workspace={workspace as never} bridge={bridge} flow={items} active={area==='items'} factsAvailable onSource={(source,recordId,column)=>route.open(source,recordId,'items',column)}/><TablesExplorer snapshot={workspace as never} bridge={bridge} tableLoading={tables} active={area==='tables'} sourceRecord={route.request} onReturnToItems={route.returnToItems}/></>;}
 const applied=()=>document.querySelector('.iv-applied');
 render(<Harness/>);await waitFor(()=>expect(document.getElementById('iv-opt-0')).toBeInTheDocument());fireEvent.click(screen.getByRole('button',{name:'Item ID'}));fireEvent.change(screen.getByRole('searchbox',{name:'Exact Item ID'}),{target:{value:'9'}});fireEvent.click(screen.getByRole('button',{name:'Search'}));await waitFor(()=>expect(applied()).toHaveTextContent('Exact Item ID 9'));fireEvent.click(document.getElementById('iv-opt-9')!);
 // A fact's source link opens its exact record with its field selected.
 const source=await screen.findByRole('button',{name:/^Item\.ClassID/});source.focus();fireEvent.click(source);
 await screen.findByRole('heading',{name:'Record #9'});expect(bridge.tableRows).toHaveBeenLastCalledWith(expect.objectContaining({tableId:'50238ec2',artifactId:s.itemSource.artifactId,recordId:'9',researchLocale:'enUS'}));expect(bridge.tablePrepare).not.toHaveBeenCalled();expect(bridge.tableData).not.toHaveBeenCalled();expect(screen.getByRole('button',{name:'Inspect record 9'})).toHaveAttribute('aria-pressed','true');
 fireEvent.click(screen.getByRole('button',{name:/Return to Items selection/}));await waitFor(()=>expect(source).toHaveFocus());expect(applied()).toHaveTextContent('Exact Item ID 9');expect(document.getElementById('iv-opt-9')).toHaveAttribute('aria-selected','true');expect(source).toHaveAttribute('tabindex','0');expect(bridge.itemsPage).toHaveBeenCalledTimes(2);expect(bridge.itemsPrepare).not.toHaveBeenCalled();
});
