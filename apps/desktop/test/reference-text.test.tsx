import {useEffect,useState} from 'react';
import {act,cleanup,configure,fireEvent,render,screen,waitFor,within} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import {afterEach,beforeEach,describe,it,expect,vi} from 'vitest';
import type {TableDataPage,TableDataSnapshot,TableDefinitionSnapshot,TableReferenceTextColumn,TableReferenceTextPage,TableReferenceTextValue} from '../../../contracts/generated/discovery';
import type {BridgeReply,DiscoveryBridge} from '../src/shared/bridge';
import type {TableRowsParams} from '../src/shared/table-data';
import {referenceTextMatches} from '../src/shared/reference-text';
import {describeReferenceText} from '../src/renderer/useReferenceText';
import {TableDataView,RecordInspector,type AdmittedRecord} from '../src/renderer/TableDataView';
import {useTableLoading} from '../src/renderer/useTableLoading';
import itemFixture from '../../../tests/desktop/fixtures/table-data-partial.json';
import classFixture from '../../../tests/desktop/fixtures/table-data.json';
import definitions from '../../../tests/desktop/fixtures/table-definitions.json';
import example from '../../../contracts/examples/table-reference-text.json';

const item=itemFixture as TableDataSnapshot,itemClass=classFixture as TableDataSnapshot;
const itemDefinition=definitions.definitions['50238ec2'] as TableDefinitionSnapshot,classDefinition=definitions.definitions.b977271e as TableDefinitionSnapshot;
const ok=<T,>(value:T):BridgeReply<T>=>({ok:true,value});
function deferred<T>(){let resolve!:(value:T)=>void;const promise=new Promise<T>(r=>resolve=r);return{promise,resolve};}
let measured={measuredEncodedBytes:'453',loadPolicy:'automatic' as 'automatic'|'onRequest'};
const large={measuredEncodedBytes:'7988612',loadPolicy:'onRequest' as const};
const classSource=(ready:boolean,scope:'complete'|'unencryptedSections'='complete')=>({tableId:'b977271e',table:'ItemClass',textColumn:'ClassName_lang',matchColumn:'ClassID',artifactId:ready?'c'.repeat(64):null,scope:ready?scope:null,...measured});
const column=(state:TableReferenceTextColumn['state'],extra:Partial<TableReferenceTextColumn>={}):TableReferenceTextColumn=>({columnIndex:1,name:'ClassID',reference:{table:'ItemClass',column:'ClassID'},state,textSource:classSource(state==='ready'),problem:['unsupported','unindexed','unreadable'].includes(state)?{code:'AUTHORED_PROBLEM',message:'Authored.'}:null,...extra});
const value=(state:TableReferenceTextValue['state'],extra:Partial<TableReferenceTextValue>={}):TableReferenceTextValue=>({columnIndex:1,value:'9',state,text:null,matchCount:['notFound','keyOutOfRange'].includes(state)?'0':state==='multiple'?'2':'1',row:['notFound','keyOutOfRange','multiple'].includes(state)?null:{id:'301',isCopy:false,copySourceId:null},...extra});
const idle={loading:false,latch:null};
// Bounded deadlock guards, not timing claims: the macOS runner can take over a second per step.
configure({asyncUtilTimeout:5000});

describe('reference text wording',()=>{
 it('gives every state its own words and keeps the number-only states plain',()=>{
  const cells=[
   describeReferenceText(column('ready'),value('resolved',{text:'Recipe'}),idle),
   describeReferenceText(column('ready'),value('empty',{text:''}),idle),
   describeReferenceText(column('ready'),value('notFound'),idle),
   describeReferenceText(column('ready',{textSource:classSource(true,'unencryptedSections')}),value('notFound'),idle),
   describeReferenceText(column('ready'),value('keyOutOfRange',{value:'300'}),idle),
   describeReferenceText(column('ready'),value('multiple',{matchCount:'2'}),idle),
   describeReferenceText(column('ready'),value('unreadable'),idle),
   describeReferenceText(column('ready'),value('textTooLong'),idle),
   describeReferenceText(column('notPrepared'),undefined,idle),
   describeReferenceText(column('notPrepared'),undefined,{loading:false,latch:{kind:'stopped',error:null}}),
   describeReferenceText(column('notPrepared'),undefined,{loading:false,latch:{kind:'failed',error:{code:'BLTE_ENCRYPTED',message:'',retryable:false}}}),
   describeReferenceText(column('notPrepared'),undefined,{loading:false,latch:{kind:'failed',error:{code:'SCHEMA_MISMATCH',message:'',retryable:false}}}),
   describeReferenceText(column('notPrepared'),undefined,{loading:false,latch:{kind:'failed',error:{code:'NETWORK_UNAVAILABLE',message:'',retryable:true}}}),
   describeReferenceText(column('unsupported',{textSource:classSource(false)}),undefined,idle),
   describeReferenceText(column('unindexed',{textSource:classSource(false)}),undefined,idle),
   describeReferenceText(column('unreadable',{textSource:classSource(false)}),undefined,idle),
  ].map(view=>view!.cell);
  expect(cells).toEqual(['Recipe','empty text','not found in ItemClass','not in readable ItemClass rows','out of range','2 matches','text not readable','text too long','…','loading stopped','not readable (protected)','unsupported layout','text unavailable','unsupported layout','unindexed','not readable']);
  const pending=describeReferenceText(column('notPrepared'),undefined,idle)!;
  expect([pending.pending,pending.sr]).toEqual([true,'text not prepared yet']);
  expect(describeReferenceText(column('notPrepared'),undefined,{loading:true,latch:null})!.sr).toBe('ItemClass text loading');
  // A large text table waits for Load text; once asked it shows the same loading hint.
  const onRequest=column('notPrepared',{textSource:{...classSource(false),...large}});
  const waiting=describeReferenceText(onRequest,undefined,idle)!;
  expect([waiting.cell,waiting.pending,waiting.styled]).toEqual(['not loaded',false,true]);
  expect(waiting.detail).toContain('≈8.0 MB encoded, measured on 12.1.5.70448');
  expect(describeReferenceText(onRequest,undefined,{loading:true,latch:null})!.cell).toBe('…');
  const material=describeReferenceText({columnIndex:3,name:'Material',reference:{table:'Material',column:'ID'},state:'noTextRule',textSource:null,problem:null},undefined,idle)!;
  expect([material.cell,material.styled,material.detail]).toEqual([null,false,'Material has no reviewed text column; number only.']);
  const tuning=describeReferenceText({columnIndex:9,name:'ContentTuningID',reference:{table:'ContentTuning',column:'ID'},state:'noDefinition',textSource:null,problem:null},undefined,idle)!;
  expect([tuning.cell,tuning.detail]).toEqual([null,'ContentTuning definition not bundled; number only.']);
 });
 it('names the redirected text table and keeps the source row for the inspector',()=>{
  const spell={columnIndex:4,name:'SpellID',reference:{table:'Spell',column:'ID'},state:'ready' as const,textSource:{tableId:'46c66698',table:'SpellName',textColumn:'Name_lang',matchColumn:'ID',artifactId:'d'.repeat(64),scope:'complete' as const,measuredEncodedBytes:'2754982',loadPolicy:'onRequest' as const},problem:null};
  const view=describeReferenceText(spell,{columnIndex:4,value:'17',state:'resolved',text:'Authored spell',matchCount:'1',row:{id:'17',isCopy:true,copySourceId:'16'}},idle)!;
  expect(view.source).toBe('SpellName.Name_lang · row ID 17 (copy of 16) · enUS');
  expect(view.detail).toContain('Declared target Spell.ID; text comes from SpellName.Name_lang by the same ID.');
  expect(describeReferenceText(column('ready'),value('notFound'),idle)!.detail).toBe('No admitted ItemClass row has ClassID = 9.');
  expect(describeReferenceText(column('ready',{textSource:classSource(true,'unencryptedSections')}),value('notFound'),idle)!.detail).toMatch(/this isn't an absence/);
 });
});

describe('reference text reply validation',()=>{
 const reply=example as TableReferenceTextPage;
 const query:TableRowsParams={productId:'wow',buildIdentity:'a'.repeat(64),sourceRegion:'us',tableId:'50238ec2',researchLocale:'enUS',artifactId:'b'.repeat(64)};
 const page:TableDataPage={...query,rows:reply.rowIds.map((id,i)=>({id,isCopy:false,copySourceId:null,values:Array.from({length:16},(_,c)=>c===1?['12','4','7','5','300','6'][i]:'0')})),totalCount:'6',nextCursor:null};
 it('accepts the authored page and refuses a reply for any other page, rule or value set',()=>{
  expect(referenceTextMatches(reply,query,page)).toBe(true);
  const bad=(change:(r:TableReferenceTextPage)=>void,p=query,shown=page)=>{const r=structuredClone(reply);change(r);return referenceTextMatches(r,p,shown);};
  expect(referenceTextMatches(reply,{...query,after:'50'},page)).toBe(false);
  expect(referenceTextMatches(reply,{...query,recordId:'900001'},page)).toBe(false);
  expect(referenceTextMatches(reply,{...query,buildIdentity:'e'.repeat(64)},page)).toBe(false);
  expect(referenceTextMatches(reply,query,{...page,rows:page.rows.slice(1)})).toBe(false);
  expect(bad(r=>{r.columns[0].textSource!.table='ItemSubClass';})).toBe(false);
  expect(bad(r=>{r.columns[0].textSource!.matchColumn='ID';})).toBe(false);
  expect(bad(r=>{r.columns[1].state='ready';})).toBe(false);
  expect(bad(r=>{r.values.push({...r.values[0],columnIndex:3});})).toBe(false);
  expect(bad(r=>{r.values.push(r.values[0]);})).toBe(false);
  expect(bad(r=>{r.values.pop();})).toBe(false);
  expect(bad(r=>{r.values[0].text='';})).toBe(false);
  expect(bad(r=>{r.values[1].text=null;})).toBe(false);
  expect(bad(r=>{r.values[2].matchCount='1';})).toBe(false);
  expect(bad(r=>{r.values[3].row={id:'1',isCopy:false,copySourceId:null};})).toBe(false);
  expect(bad(r=>{r.columns=[r.columns[1],r.columns[0],r.columns[2]];})).toBe(false);
  expect(bad(r=>{r.columns[0].textSource!.loadPolicy='onRequest';})).toBe(false);
  expect(bad(r=>{r.columns[0].textSource!.measuredEncodedBytes='1048577';})).toBe(false);
  expect(bad(r=>{r.columns[0].textSource!.measuredEncodedBytes='1048577';r.columns[0].textSource!.loadPolicy='onRequest';})).toBe(true);
  // A reference column the snapshot doesn't declare cannot gain text.
  expect(referenceTextMatches(reply,query,page,{...item,artifactId:query.artifactId,columns:item.columns.map(c=>({...c,reference:null}))})).toBe(false);
 });
});

function setup(options:{classReady?:boolean;prepare?:Promise<BridgeReply<TableDataSnapshot>>;large?:boolean;busy?:boolean}={}){
 let classReady=!!options.classReady;measured=options.large?large:{measuredEncodedBytes:'453',loadPolicy:'automatic'};
 const rows=(p:TableRowsParams)=>{const after=Number(p.after??0),all=Array.from({length:60},(_,i)=>({id:String(i),isCopy:false,copySourceId:null,values:item.columns.map((c,index)=>c.isId?String(i):String(i+index+1))})),filtered=p.recordId===undefined?all:all.filter(x=>x.id===p.recordId);return {filtered,rows:filtered.slice(after,after+(p.limit??50))};};
 const textFor=(p:TableRowsParams):TableReferenceTextPage=>{const {rows:page}=rows(p),columns=item.columns.flatMap((c,i)=>!c.reference?[]:[c.reference.table==='ItemClass'?column(classReady?'ready':'notPrepared',{columnIndex:i}):{columnIndex:i,name:c.name,reference:c.reference,state:['Material','ItemGroupSounds'].includes(c.reference.table)?'noTextRule' as const:'noDefinition' as const,textSource:null,problem:null}]);
  const values=classReady?[...new Set(page.map(r=>r.values[1]))].map(v=>v==='3'?value('empty',{value:v,text:'',row:{id:'303',isCopy:false,copySourceId:null}}):v==='4'?value('multiple',{value:v}):v==='5'?value('notFound',{value:v}):value('resolved',{value:v,text:`Authored class ${v}`,row:{id:String(300+Number(v)),isCopy:false,copySourceId:null}})):[];
  return {productId:p.productId,buildIdentity:p.buildIdentity,sourceRegion:p.sourceRegion,tableId:p.tableId,researchLocale:'enUS',artifactId:p.artifactId,after:p.after??'0',recordId:p.recordId??null,policyRevision:'table-reference-text-v1',rowIds:page.map(r=>r.id),columns,values};};
 const prepare=options.prepare??deferred<BridgeReply<TableDataSnapshot>>().promise;
 const bridge={tableData:vi.fn(async()=>ok({snapshot:item})),tablePrepare:vi.fn(()=>prepare),
  tableRows:vi.fn(async(p:TableRowsParams)=>{const {filtered,rows:page}=rows(p),after=Number(p.after??0);return ok({...p,rows:page,totalCount:String(filtered.length),nextCursor:after+page.length<filtered.length?String(after+page.length):null});}),
  tableReferenceText:vi.fn(async(p:TableRowsParams)=>ok(textFor(p))),
  tableDefinition:vi.fn(async(p:{productId:string;buildIdentity:string;sourceRegion:string;tableId:string})=>ok({...classDefinition,productId:p.productId,buildIdentity:p.buildIdentity,sourceRegion:p.sourceRegion})),
  cancel:vi.fn(async()=>ok({requested:true})),onProgress:vi.fn(()=>()=>{})} as unknown as DiscoveryBridge;
 function Harness(){const flow=useTableLoading(bridge),[record,setRecord]=useState<AdmittedRecord|null>(null);useEffect(()=>{if(options.busy)flow.load(definitions.definitions.a4c11069 as TableDefinitionSnapshot);},[]);return <><button>Elsewhere</button><TableDataView definition={itemDefinition} bridge={bridge} active={true} flow={flow} focusRequest={0} onRecord={setRecord}/>{record&&<aside aria-label="Inspector"><RecordInspector record={record}/></aside>}</>;}
 return {bridge,textFor,makeReady:()=>{classReady=true;},...render(<Harness/>)};
}
const cell=(record:string)=>screen.getByRole('button',{name:`Inspect record ${record}`}).closest('tr')!.querySelectorAll('td')[1] as HTMLElement;
beforeEach(()=>{Element.prototype.scrollIntoView=vi.fn();});
afterEach(cleanup);

describe('reference text in the Data grid and inspector',{timeout:30000},()=>{
 it('shows a pending hint, prepares the text table automatically and fills in without moving focus',async()=>{
  const prepared=deferred<BridgeReply<TableDataSnapshot>>();const {bridge,makeReady}=setup({prepare:prepared.promise});
  await screen.findByRole('button',{name:'Inspect record 0'});
  await waitFor(()=>expect(cell('1').textContent).toBe('3 · …, text not prepared yet'));
  expect(within(cell('1')).getByText(', text not prepared yet')).toHaveClass('visually-hidden');
  await waitFor(()=>expect(bridge.tablePrepare).toHaveBeenCalledWith(expect.objectContaining({tableId:'b977271e',researchLocale:'enUS'}),expect.any(String)));
  expect(bridge.tableDefinition).toHaveBeenCalledTimes(1);
  // Every declared reference gets the reference style; Material has no text rule, so it shows the number only.
  const material=cell('1').parentElement!.querySelectorAll('td')[3] as HTMLElement;
  expect(material).toHaveClass('dh-refcell');expect(material.textContent).toBe('5');expect(material.querySelector('.dh-ref')).not.toBeNull();expect(material.querySelector('.dh-rt')).toBeNull();
  const elsewhere=screen.getByRole('button',{name:'Elsewhere'});elsewhere.focus();
  makeReady();await act(async()=>prepared.resolve(ok({...itemClass,productId:item.productId,buildIdentity:item.buildIdentity,sourceRegion:item.sourceRegion})));
  await waitFor(()=>expect(cell('0').textContent).toBe('2 · Authored class 2'));
  expect(cell('1').textContent).toBe('3 · empty text');
  expect(cell('2').textContent).toBe('4 · 2 matches');
  expect(cell('3').textContent).toBe('5 · not found in ItemClass');
  expect(within(cell('0')).getByText('2')).toHaveClass('dh-ref');
  expect(elsewhere).toHaveFocus();
  expect(bridge.tablePrepare).toHaveBeenCalledTimes(1);
  fireEvent.click(screen.getByRole('button',{name:'Inspect record 0'}));
  const inspector=screen.getByRole('complementary',{name:'Inspector'});
  expect(within(inspector).getByText('Authored class 2',{selector:'b'})).toBeVisible();
  expect(within(inspector).getByText('ItemClass.ClassName_lang · row ID 302 · enUS')).toBeVisible();
  expect(within(inspector).getByText('Material has no reviewed text column; number only.')).toBeVisible();
  // The raw value remains the inspector value and what is copied.
  expect(inspector.querySelector('[data-source-column="1"] .dh-fval')!.textContent).toBe('2');
 });
 it('waits for Load text on a large text table, loads it on request and keeps focus usable',async()=>{
  const prepared=deferred<BridgeReply<TableDataSnapshot>>();const {bridge,makeReady}=setup({prepare:prepared.promise,large:true});
  await screen.findByRole('button',{name:'Inspect record 0'});
  await waitFor(()=>expect(cell('1').textContent).toBe('3 · not loaded'));
  const notice=screen.getByRole('region',{name:'ItemClass text'});
  const load=await within(notice).findByRole('button',{name:'Load text (≈8.0 MB)'});
  await waitFor(()=>expect(load).toHaveAttribute('aria-disabled','false'));
  expect(bridge.tablePrepare).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button',{name:'Inspect record 0'}));
  const inspector=screen.getByRole('complementary',{name:'Inspector'});
  expect(within(inspector).getByRole('button',{name:'Load text (≈8.0 MB)'})).toBeVisible();
  expect(within(inspector).getByText(/loads only when you choose Load text/)).toBeVisible();
  load.focus();fireEvent.click(load);
  await waitFor(()=>expect(bridge.tablePrepare).toHaveBeenCalledWith(expect.objectContaining({tableId:'b977271e'}),expect.any(String)));
  expect(within(notice).getByRole('button',{name:'Loading ItemClass text…'})).toHaveAttribute('aria-disabled','true');
  expect(within(notice).getByRole('button',{name:'Loading ItemClass text…'})).toHaveFocus();
  expect(cell('1').textContent).toBe('3 · …, ItemClass text loading');
  makeReady();await act(async()=>prepared.resolve(ok({...itemClass,productId:item.productId,buildIdentity:item.buildIdentity,sourceRegion:item.sourceRegion})));
  await waitFor(()=>expect(cell('0').textContent).toBe('2 · Authored class 2'));
  expect(screen.queryByRole('region',{name:'ItemClass text'})).not.toBeInTheDocument();
  expect(screen.getByRole('region',{name:/admitted table rows/})).toHaveFocus();
  expect(bridge.tablePrepare).toHaveBeenCalledTimes(1);
  // The inspector follows the new reply for the same page.
  await waitFor(()=>expect(within(inspector).getByText('Authored class 2',{selector:'b'})).toBeVisible());
  expect(within(inspector).queryByText(/loads only when you choose Load text/)).not.toBeInTheDocument();
  expect(within(inspector).queryByRole('button',{name:/Load text|Loading ItemClass/})).not.toBeInTheDocument();
 });
 it('keeps a failed definition read visible with a retry that asks again, for large and automatic tables',async()=>{
  for(const large of [true,false]){
   const {bridge}=setup({large});
   vi.mocked(bridge.tableDefinition).mockResolvedValueOnce({ok:false,error:{code:'BUSY',message:'Authored busy engine.',retryable:true}});
   await screen.findByRole('button',{name:'Inspect record 0'});
   const notice=await screen.findByRole('region',{name:'ItemClass text'});
   await waitFor(()=>expect(within(notice).getByText('The ItemClass definition could not be read, so its text can\'t load yet.')).toBeVisible());
   expect(notice.querySelector('strong')!.textContent).toBe('ItemClass text not loaded');
   expect(notice.querySelector('.reference-code code')!.textContent).toBe('(BUSY)');
   expect(within(notice).queryByText(/References show numbers until you load/)).not.toBeInTheDocument();
   expect(cell('1').textContent).toBe('3 · text unavailable');
   const retry=within(notice).getByRole('button',{name:'Retry ItemClass definition'});
   expect(retry).toHaveAttribute('aria-disabled','false');
   expect(bridge.tablePrepare).not.toHaveBeenCalled();
   fireEvent.click(retry);
   await waitFor(()=>expect(bridge.tableDefinition).toHaveBeenCalledTimes(2));
   if(large)await waitFor(()=>expect(within(notice).getByRole('button',{name:'Load text (≈8.0 MB)'})).toHaveAttribute('aria-disabled','false'));
   else await waitFor(()=>expect(bridge.tablePrepare).toHaveBeenCalledWith(expect.objectContaining({tableId:'b977271e'}),expect.any(String)));
   cleanup();
  }
 });
 it('returns focus to the inspector field when the inspector Load text disappears',async()=>{
  const prepared=deferred<BridgeReply<TableDataSnapshot>>();const {makeReady}=setup({prepare:prepared.promise,large:true});
  await screen.findByRole('button',{name:'Inspect record 0'});
  fireEvent.click(screen.getByRole('button',{name:'Inspect record 0'}));
  const inspector=screen.getByRole('complementary',{name:'Inspector'});
  const load=await within(inspector).findByRole('button',{name:'Load text (≈8.0 MB)'});
  await waitFor(()=>expect(load).toHaveAttribute('aria-disabled','false'));
  load.focus();fireEvent.click(load);
  await waitFor(()=>expect(within(inspector).getByRole('button',{name:'Loading ItemClass text…'})).toHaveFocus());
  makeReady();await act(async()=>prepared.resolve(ok({...itemClass,productId:item.productId,buildIdentity:item.buildIdentity,sourceRegion:item.sourceRegion})));
  await waitFor(()=>expect(within(inspector).getByText('Authored class 2',{selector:'b'})).toBeVisible());
  expect(inspector.querySelector('[data-source-column="1"]')).toHaveFocus();
  expect(screen.getByRole('region',{name:/admitted table rows/})).not.toHaveFocus();
 });
 it('gives a failed text load a plain heading, the code at normal weight and a scoped Retry',async()=>{
  const {bridge}=setup({large:true,prepare:Promise.resolve({ok:false,error:{code:'NETWORK_UNAVAILABLE',message:'Authored offline.',retryable:true}})});
  await screen.findByRole('button',{name:'Inspect record 0'});
  const notice=await screen.findByRole('region',{name:'ItemClass text'});
  fireEvent.click(await within(notice).findByRole('button',{name:'Load text (≈8.0 MB)'}));
  await waitFor(()=>expect(notice.querySelector('strong')!.textContent).toBe('ItemClass text could not be loaded'));
  expect(notice.querySelector('.reference-code code')!.textContent).toBe('(NETWORK_UNAVAILABLE)');
  expect(within(notice).getByRole('button',{name:'Retry ItemClass text'})).toBeVisible();
  expect(cell('1').textContent).toBe('3 · text unavailable');
  expect(bridge.tablePrepare).toHaveBeenCalledTimes(1);
 });
 it('returns focus to the inspector field when a non-retryable failure removes its button, and says each failure once',async()=>{
  const prepared=deferred<BridgeReply<TableDataSnapshot>>();setup({prepare:prepared.promise,large:true});
  await screen.findByRole('button',{name:'Inspect record 0'});
  fireEvent.click(screen.getByRole('button',{name:'Inspect record 0'}));
  const inspector=screen.getByRole('complementary',{name:'Inspector'});
  const load=await within(inspector).findByRole('button',{name:'Load text (≈8.0 MB)'});
  await waitFor(()=>expect(load).toHaveAttribute('aria-disabled','false'));
  load.focus();fireEvent.click(load);
  await act(async()=>prepared.resolve({ok:false,error:{code:'BLTE_ENCRYPTED',message:'Authored protected metadata.',retryable:false}}));
  await waitFor(()=>expect(within(inspector).getByText(/ItemClass is protected and could not be read \(BLTE_ENCRYPTED\)/)).toBeVisible());
  expect(within(inspector).queryByRole('button',{name:/Retry ItemClass|Load text/})).not.toBeInTheDocument();
  expect(inspector.querySelector('[data-source-column="1"]')).toHaveFocus();
  expect(within(inspector.querySelector('[data-source-column="1"]') as HTMLElement).getAllByText(/BLTE_ENCRYPTED/)).toHaveLength(1);
 });
 it('says a failed definition read once in the inspector',async()=>{
  const {bridge}=setup({large:true});
  vi.mocked(bridge.tableDefinition).mockResolvedValueOnce({ok:false,error:{code:'BUSY',message:'Authored busy engine.',retryable:true}});
  await screen.findByRole('button',{name:'Inspect record 0'});
  await screen.findByRole('region',{name:'ItemClass text'});
  fireEvent.click(screen.getByRole('button',{name:'Inspect record 0'}));
  const field=screen.getByRole('complementary',{name:'Inspector'}).querySelector('[data-source-column="1"]') as HTMLElement;
  await waitFor(()=>expect(within(field).getByRole('button',{name:'Retry ItemClass definition'})).toBeVisible());
  expect(within(field).getAllByText(/definition could not be read/)).toHaveLength(1);
 });
 it('shows why Load text is unavailable as visible text while another table loads',async()=>{
  const {bridge}=setup({large:true,busy:true});
  await screen.findByRole('button',{name:'Inspect record 0'});
  const notice=await screen.findByRole('region',{name:'ItemClass text'});
  const load=await within(notice).findByRole('button',{name:'Load text (≈8.0 MB)'});
  await waitFor(()=>expect(within(notice).getByText('Another table is loading. This becomes available when it finishes.')).toBeVisible());
  expect(load).toHaveAttribute('aria-disabled','true');expect(load).not.toHaveAttribute('title');
  expect(load).toHaveAccessibleDescription('Another table is loading. This becomes available when it finishes.');
  fireEvent.click(load);
  expect(bridge.tablePrepare).toHaveBeenCalledTimes(1);
  expect(vi.mocked(bridge.tablePrepare).mock.calls[0][0]).toMatchObject({tableId:'a4c11069'});
 });
 it('keeps a stopped load in its notice with a scoped Retry that reads differently from never loaded',async()=>{
  const {bridge,makeReady}=setup({large:true,prepare:Promise.resolve({ok:false,error:{code:'CANCELED',message:'Authored stop.',retryable:true}})});
  await screen.findByRole('button',{name:'Inspect record 0'});
  const notice=await screen.findByRole('region',{name:'ItemClass text'});
  fireEvent.click(await within(notice).findByRole('button',{name:'Load text (≈8.0 MB)'}));
  await waitFor(()=>expect(within(notice).getByText('ItemClass text not loaded · loading stopped')).toBeVisible());
  expect(cell('1').textContent).toBe('3 · loading stopped');
  fireEvent.click(screen.getByRole('button',{name:'Inspect record 1'}));
  expect(within(screen.getByRole('complementary',{name:'Inspector'})).getByText(/loading was stopped/)).toBeVisible();
  const retry=within(notice).getByRole('button',{name:'Retry ItemClass text'});
  const prepared=deferred<BridgeReply<TableDataSnapshot>>();vi.mocked(bridge.tablePrepare).mockReturnValueOnce(prepared.promise);
  retry.focus();fireEvent.click(retry);
  expect(bridge.tablePrepare).toHaveBeenCalledTimes(2);
  await waitFor(()=>expect(within(notice).getByRole('button',{name:'Loading ItemClass text…'})).toHaveFocus());
  makeReady();await act(async()=>prepared.resolve(ok({...itemClass,productId:item.productId,buildIdentity:item.buildIdentity,sourceRegion:item.sourceRegion})));
  await waitFor(()=>expect(cell('1').textContent).toBe('3 · empty text'));
  expect(screen.queryByRole('region',{name:'ItemClass text'})).not.toBeInTheDocument();
 });
 it('shows a failed batch above the grid and in the inspector, and retries it when retryable',async()=>{
  const {bridge}=setup({classReady:true});
  vi.mocked(bridge.tableReferenceText!).mockResolvedValueOnce({ok:false,error:{code:'BUSY',message:'Authored busy engine.',retryable:true}});
  await screen.findByRole('button',{name:'Inspect record 0'});
  const notice=await screen.findByRole('region',{name:'Reference text'});
  expect(within(notice).getByText('(BUSY)')).toBeVisible();
  // Plain heading; the code sits in the detail line at normal weight.
  expect(notice.querySelector('strong')!.textContent).toBe('Reference text unavailable for this page');
  expect(within(notice).getByText('(BUSY)').closest('.reference-code')).not.toBeNull();
  expect(cell('0').textContent).toBe('2');
  fireEvent.click(screen.getByRole('button',{name:'Inspect record 0'}));
  expect(screen.getByRole('complementary',{name:'Inspector'}).querySelector('[data-source-column="1"]')).toHaveTextContent('Reference text unavailable for this page (BUSY). Raw value only.');
  const retry=within(notice).getByRole('button',{name:'Retry reference text'});retry.focus();fireEvent.click(retry);
  await waitFor(()=>expect(cell('0').textContent).toBe('2 · Authored class 2'));
  expect(screen.queryByRole('region',{name:'Reference text'})).not.toBeInTheDocument();
  expect(screen.getByRole('region',{name:/admitted table rows/})).toHaveFocus();
  await waitFor(()=>expect(within(screen.getByRole('complementary',{name:'Inspector'})).getByText('Authored class 2',{selector:'b'})).toBeVisible());
  expect(bridge.tableReferenceText).toHaveBeenCalledTimes(2);
 });
 it('drops a reply that arrives after the page changed and asks once per page',async()=>{
  const {bridge,textFor}=setup({classReady:true});
  await waitFor(()=>expect(cell('0').textContent).toBe('2 · Authored class 2'));
  const held=deferred<BridgeReply<TableReferenceTextPage>>();
  vi.mocked(bridge.tableReferenceText!).mockReturnValueOnce(held.promise);
  fireEvent.click(screen.getByRole('button',{name:'Next page of records'}));
  await screen.findByRole('button',{name:'Inspect record 50'});
  // The held reply was for page two; a newer page-one reply cannot land on page two either.
  fireEvent.click(screen.getByRole('button',{name:'First page of records'}));
  await screen.findByRole('button',{name:'Inspect record 0'});
  await waitFor(()=>expect(cell('0').textContent).toBe('2 · Authored class 2'));
  await act(async()=>held.resolve(ok(textFor({productId:item.productId,buildIdentity:item.buildIdentity,sourceRegion:item.sourceRegion,tableId:item.tableId,researchLocale:'enUS',artifactId:item.artifactId,after:'50'}))));
  expect(cell('0').textContent).toBe('2 · Authored class 2');
  const calls=vi.mocked(bridge.tableReferenceText!).mock.calls.map(c=>c[0].after??'0');
  expect(calls).toEqual(['0','50','0']);
  expect(bridge.tablePrepare).not.toHaveBeenCalled();
 });
 it('latches a protected text table as not readable and never retries it automatically',async()=>{
  const {bridge}=setup({prepare:Promise.resolve({ok:false,error:{code:'BLTE_ENCRYPTED',message:'Authored protected metadata.',retryable:false}})});
  await screen.findByRole('button',{name:'Inspect record 0'});
  await waitFor(()=>expect(cell('0').textContent).toBe('2 · not readable (protected)'));
  fireEvent.click(screen.getByRole('button',{name:'Next page of records'}));
  await screen.findByRole('button',{name:'Inspect record 50'});
  await waitFor(()=>expect(cell('50').textContent).toBe('52 · not readable (protected)'));
  expect(bridge.tablePrepare).toHaveBeenCalledTimes(1);
  // The outcome stays visible; Retry wouldn't change a protected table.
  const notice=screen.getByRole('region',{name:'ItemClass text'});
  expect(notice.querySelector('strong')!.textContent).toBe('ItemClass text not readable (protected)');
  expect(notice.querySelector('.reference-code code')!.textContent).toBe('(BLTE_ENCRYPTED)');
  expect(within(notice).queryByRole('button')).not.toBeInTheDocument();
 });
 it('shows numbers only when the engine has no reference text and rejects a mismatched reply',async()=>{
  const {bridge}=setup();vi.mocked(bridge.tableReferenceText!).mockResolvedValue({ok:false,error:{code:'UNSUPPORTED',message:'Older engine.',retryable:false}});
  await screen.findByRole('button',{name:'Inspect record 0'});
  await waitFor(()=>expect(bridge.tableReferenceText).toHaveBeenCalled());
  expect(cell('0').textContent).toBe('2'); expect(cell('0').querySelector('.dh-rt')).toBeNull();
  expect(screen.queryByRole('region',{name:'Reference text'})).not.toBeInTheDocument();
  cleanup();
  const second=setup({classReady:true});vi.mocked(second.bridge.tableReferenceText!).mockImplementation(async p=>ok({...second.textFor(p),after:'7'}));
  await screen.findByRole('button',{name:'Inspect record 0'});
  await waitFor(()=>expect(second.bridge.tableReferenceText).toHaveBeenCalled());
  expect(cell('0').textContent).toBe('2');
  const mismatch=await screen.findByRole('region',{name:'Reference text'});
  expect(within(mismatch).getByText('(TABLE_REFERENCE_TEXT_MISMATCH)')).toBeVisible();
  expect(within(mismatch).queryByRole('button')).not.toBeInTheDocument();
  expect(second.bridge.tablePrepare).not.toHaveBeenCalled();
 });
});
