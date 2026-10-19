import {useState} from 'react';
import {cleanup,fireEvent,render,screen,waitFor} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import {afterEach,describe,it,expect,vi} from 'vitest';
import type {DiscoveryBridge} from '../src/shared/bridge';
import type {TableDataSnapshot,TableDefinitionSnapshot} from '../../../contracts/generated/discovery';
import {TableDataView,RecordInspector,type AdmittedRecord} from '../src/renderer/TableDataView';
import {useItemSourceNavigation,type SourceRecordRequest} from '../src/renderer/useItemSourceNavigation';
import {useTableLoading} from '../src/renderer/useTableLoading';
import fixture from '../../../tests/desktop/fixtures/table-data.json';
import definitions from '../../../tests/desktop/fixtures/table-definitions.json';

const snapshot=fixture as TableDataSnapshot,definition=definitions.definitions.b977271e as TableDefinitionSnapshot;
HTMLElement.prototype.scrollIntoView=vi.fn();
const row={id:'7',isCopy:false,copySourceId:null,values:['7','Authored exact occurrence','0','1','0']};
function setup(change:{id?:string;isCopy?:boolean;copySourceId?:string|null;values?:readonly string[]}={}){
 const request={id:1,origin:'items',kind:'ordinal',snapshot,recordId:'7',sourceOrdinal:'2',isCopy:false,copySourceId:null,expectedValues:[{columnIndex:1,value:'Authored exact occurrence'},{columnIndex:3,value:'1'}]} as SourceRecordRequest;
 const bridge={tableData:vi.fn(),tablePrepare:vi.fn(),cancel:vi.fn(),onProgress:()=>()=>{},tableRows:vi.fn(async(p)=>({ok:true,value:{productId:p.productId,buildIdentity:p.buildIdentity,sourceRegion:p.sourceRegion,tableId:p.tableId,researchLocale:p.researchLocale,artifactId:p.artifactId,rows:[{...row,...change}],totalCount:'60',nextCursor:'3'}}))} as unknown as DiscoveryBridge;
 function Harness(){const flow=useTableLoading(bridge),[record,setRecord]=useState<AdmittedRecord|null>(null);return <><TableDataView definition={definition} bridge={bridge} active flow={flow} onRecord={setRecord} focusRequest={0} sourceRecord={request}/>{record&&<RecordInspector record={record}/>}</>;}
 render(<Harness/>);return{bridge};
}
afterEach(cleanup);
describe('exact original source occurrence routing',()=>{
 it('reads one original ordinal without an ID filter and selects that occurrence without preparation',async()=>{const{bridge}=setup();await screen.findByRole('heading',{name:'Record #7'});expect(bridge.tableRows).toHaveBeenCalledWith(expect.objectContaining({after:'2',limit:1,artifactId:snapshot.artifactId}));expect(vi.mocked(bridge.tableRows).mock.calls[0][0]).not.toHaveProperty('recordId');expect(bridge.tableData).not.toHaveBeenCalled();expect(bridge.tablePrepare).not.toHaveBeenCalled();expect(screen.getByText('Authored exact occurrence',{selector:'.definition-fact b'})).toBeVisible();});
 it.each([['emitted ID',{id:'8'}],['copy identity',{isCopy:true,copySourceId:'1'}],['original cell',{values:['7','Wrong occurrence','0','1','0']}]] as const)('refuses another %s instead of choosing a matching-looking row',async(_name,change)=>{const{bridge}=setup(change);await waitFor(()=>expect(screen.getByText('SOURCE_OCCURRENCE_MISMATCH')).toBeVisible());expect(screen.queryByRole('heading',{name:/Record #/})).not.toBeInTheDocument();expect(bridge.tableRows).toHaveBeenCalledTimes(1);expect(bridge.tablePrepare).not.toHaveBeenCalled();});
});

function SourceReturn(){const[pane,setPane]=useState<'items'|'creatures'|'tables'>('items'),[revision,setRevision]=useState(0),source=useItemSourceNavigation(pane,setPane),key=`exact-occurrence|name|${revision}`;return <><section className="items-browser" hidden={pane!=='items'}><section className="item-effects"><h3 tabIndex={-1}>Effects fallback</h3><article data-effect-occurrence="same-occurrence"><details open><summary>Source records and inputs</summary><div className="effect-evidence-body" data-testid="inner-scroll"><button data-source-origin={key} onClick={()=>source.openTarget(snapshot,{recordId:'7',isCopy:false,copySourceId:null},key)}>Original target</button></div></details></article></section></section><section hidden={pane!=='tables'}><button onClick={()=>setRevision(x=>x+1)}>Change original target authority</button><button onClick={source.returnToItems}>Return to effects</button></section></>;}
describe('effects source return continuity',()=>{
 it('restores actual invoking action and inner evidence scroll only for the same complete source key',()=>{const scroll=vi.spyOn(window,'scrollTo').mockImplementation(()=>{});render(<SourceReturn/>);const body=screen.getByTestId('inner-scroll'),source=screen.getByRole('button',{name:'Original target'});body.scrollTop=93;source.focus();fireEvent.click(source);body.scrollTop=0;fireEvent.click(screen.getByRole('button',{name:'Return to effects'}));expect(source).toHaveFocus();expect(body.scrollTop).toBe(93);expect(scroll).toHaveBeenCalledTimes(1);scroll.mockRestore();});
 it('does not restore stale window/inner scroll to a reused source button with changed authority',()=>{const scroll=vi.spyOn(window,'scrollTo').mockImplementation(()=>{});render(<SourceReturn/>);const body=screen.getByTestId('inner-scroll'),source=screen.getByRole('button',{name:'Original target'});body.scrollTop=93;source.focus();fireEvent.click(source);fireEvent.click(screen.getByRole('button',{name:'Change original target authority'}));body.scrollTop=0;fireEvent.click(screen.getByRole('button',{name:'Return to effects'}));expect(screen.getByText('Source records and inputs')).toHaveFocus();expect(body.scrollTop).toBe(0);expect(scroll).not.toHaveBeenCalled();scroll.mockRestore();});
});

it('binds highlighted duplicate emitted IDs to their exact original ordinal',async()=>{const bridge={onProgress:()=>()=>{},tableData:vi.fn(async()=>({ok:true,value:{snapshot}})),tableRows:vi.fn(async p=>({ok:true,value:{productId:p.productId,buildIdentity:p.buildIdentity,sourceRegion:p.sourceRegion,tableId:p.tableId,researchLocale:p.researchLocale,artifactId:p.artifactId,rows:[row,{...row,values:['7','Second original occurrence','0','1','0']}],totalCount:'60',nextCursor:'2'}}))} as unknown as DiscoveryBridge;function Harness(){const flow=useTableLoading(bridge);return <TableDataView bridge={bridge} definition={definition} active flow={flow} focusRequest={0} onRecord={()=>{}}/>;}render(<Harness/>);const buttons=await screen.findAllByRole('button',{name:'Inspect record 7'});fireEvent.click(buttons[1]);expect(buttons[1]).toHaveAttribute('aria-pressed','true');expect(buttons[0]).toHaveAttribute('aria-pressed','false');fireEvent.click(buttons[0]);expect(buttons[0]).toHaveAttribute('aria-pressed','true');expect(buttons[1]).toHaveAttribute('aria-pressed','false');});
