import {createRef} from 'react';
import {cleanup,render,screen,within} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import {afterEach,it,expect} from 'vitest';
import type {ItemFact,ItemFactsResult,ItemFactValue} from '../../../contracts/generated/discovery';
import {ItemDetailView,SharedFact,labelMaps} from '../src/renderer/ItemsBrowser';
import {itemsSnapshot,itemDetail,itemFacts,itemLabelLists} from '../../../tests/desktop/fixtures/items.mjs';
// Classic-shaped facts (docs/design/items-view, D098): stored stat amounts and fields Item and ItemSparse store differently.
afterEach(cleanup);
const snapshot=itemsSnapshot();
const cell=(tableId:'50238ec2'|'919be54e',column:string,columnIndex:number,value:string):ItemFactValue=>({tableId,table:tableId==='50238ec2'?'Item':'ItemSparse',column,columnIndex,recordId:'3',value,label:null});
function view(change:(facts:ItemFact[])=>void){
 const result=structuredClone(itemFacts(snapshot,{itemId:'3'})) as ItemFactsResult;change(result.facts as ItemFact[]);
 return render(<ItemDetailView snapshot={snapshot} item={result.facts?itemDetail(snapshot,'3')!.summary:itemDetail(snapshot,'3')!.summary} detail={itemDetail(snapshot,'3')} facts={result} factsAvailable factsError={null} detailError={null} busy={false} labels={labelMaps(itemLabelLists)} open={()=>{}} compact={false} headingRef={createRef()} onRetry={()=>{}} icon={null} iconText={null} classificationPending={false} classificationBlocked={null} classificationsAvailable={false} subclassAvailable={false} effects={null}/>);
}
const set=(facts:ItemFact[],key:string,fact:Partial<ItemFact>)=>{const i=facts.findIndex(f=>f.key===key);facts[i]={...facts[i]!,...fact} as ItemFact;};

it('shows Classic stored stat amounts as stored amounts, without the allocation note',()=>{
 view(facts=>{
  set(facts,'statType',{state:'value',agreement:'single',values:Array.from({length:10},(_,i)=>cell('919be54e',`StatModifier_bonusStat[${i}]`,13+i,i<2?['3','7'][i]!:'-1')) as ItemFact['values']});
  set(facts,'statAmount',{state:'value',agreement:'single',values:Array.from({length:10},(_,i)=>cell('919be54e',`StatModifier_bonusAmount[${i}]`,23+i,i<2?['5','8'][i]!:'0')) as ItemFact['values']});
  for(const key of['statPercentEditor','statSocketShare'])set(facts,key,{state:'unsupported',agreement:'single',values:[]});
 });
 expect(screen.getByRole('heading',{name:'Stats · stored amounts'})).toBeVisible();
 const table=screen.getByRole('region',{name:'Stats, 10 slots'});
 expect(within(table).getByRole('columnheader',{name:/Amount/})).toBeVisible();
 expect(within(table).queryByRole('columnheader',{name:/Percent editor/})).not.toBeInTheDocument();
 expect(within(table).getAllByRole('row')[1]).toHaveTextContent('035');
 expect(screen.getByText(/These are the stat amounts this layout stores/)).toBeVisible();
 expect(screen.queryByText(/Final \+N depends on item level/)).not.toBeInTheDocument();
});

it('keeps the retail allocation heading and note when no stored amounts exist',()=>{
 view(()=>{});
 expect(screen.getByRole('heading',{name:'Stat allocation, not final stats'})).toBeVisible();
 expect(screen.getByText(/Final \+N depends on item level, slot and bonuses/)).toBeVisible();
});

it('shows a field Item and ItemSparse store differently as one fact per table, with every element',()=>{
 const fact:ItemFact={key:'damageType',state:'value',agreement:'separate',values:[...Array.from({length:5},(_,i)=>cell('50238ec2',`DamageType[${i}]`,1+i,i===1?'3':'0')),cell('919be54e','DamageType',6,'0')] as ItemFact['values']};
 render(<SharedFact label="DamageType" fact={fact} open={()=>{}} render={v=>v.value}/>);
 expect(screen.getByText('DamageType · Item')).toBeVisible();expect(screen.getByText('DamageType · ItemSparse')).toBeVisible();
 expect(screen.getByText('[0, 3, 0, 0, 0]')).toBeVisible();
 expect(screen.getByText('Item and ItemSparse store this field differently. Both are kept as read.')).toBeVisible();
 expect(screen.getAllByRole('button')).toHaveLength(6);
});

it('shows agreeing copies once, and disagreeing copies once per table',()=>{
 const agree:ItemFact={key:'requiredLevel',state:'value',agreement:'agree',values:[cell('50238ec2','RequiredLevel',9,'60'),cell('919be54e','RequiredLevel',6,'60')] as ItemFact['values']};
 const{unmount}=render(<SharedFact label="Required level" fact={agree} open={()=>{}} render={v=>v.value}/>);
 expect(screen.getByText('60')).toBeVisible();expect(screen.getAllByRole('button')).toHaveLength(2);expect(screen.queryByText(/disagree/)).not.toBeInTheDocument();
 unmount();
 render(<SharedFact label="Required level" fact={{...agree,agreement:'disagree',values:[agree.values[0]!,{...agree.values[1]!,value:'58'}] as ItemFact['values']}} open={()=>{}} render={v=>v.value}/>);
 expect(screen.getByText('Required level · Item')).toBeVisible();expect(screen.getByText('58')).toBeVisible();expect(screen.getByText('Item and ItemSparse disagree.')).toBeVisible();
});
