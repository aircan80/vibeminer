import type {CreatureDisplayMetadataResult,CreatureGeometryMetadata,CreatureGeometryResult,EngineProgress,OpeningSourceReference} from '../../../../contracts/generated/discovery';
import type {DiscoveryBridge} from '../shared/bridge';
import type {GeometryParams} from '../shared/creature-geometry';
import {geometrySelectionKey} from '../shared/creature-geometry';
import {creatureDisplayMatches} from '../shared/creature-display';
import type {GeometrySection} from './geometry-vmg';

export type GeometryFailure=Readonly<{code:string;message:string;retryable:boolean}>;
export type GeometryEntryAnchor=Readonly<{opening:OpeningSourceReference;metadata:CreatureGeometryMetadata}>;
export type GeometryReturnOrigin=Readonly<{key:string;invoker:HTMLElement|null;windowScroll:readonly[number,number];innerScroll:readonly Readonly<{element:HTMLElement;left:number;top:number}>[]}>;
export type GeometryIntent=Readonly<{selection:GeometryParams;anchor:GeometryEntryAnchor;label:Readonly<{name:string;buildVersion:string}>;origin:GeometryReturnOrigin}>;
export type GeometryLongAdmission=Readonly<{tryReserve:()=> (()=>void)|null;revision:number}>;
export type GeometryPhase='entering'|'checking'|'waiting'|'preparing'|'receiving'|'validating'|'ready'|'outcome'|'stopping'|'releasing'|'blocked';
export type GeometryViewState=Readonly<{intent:GeometryIntent;generation:number;phase:GeometryPhase;message:string;result:CreatureGeometryResult|null;error:GeometryFailure|null;canMount:boolean;sections:readonly GeometrySection[];sectionOffset:number}>;
export type GeometryOperation=Readonly<{id:string;intent:GeometryIntent;status:'pending'|'stopping'|'succeeded'|'failed'|'canceled';progress:EngineProgress|null;error:GeometryFailure|null}>;
export type GeometryRetirement=Readonly<{intent:GeometryIntent;phase:'releasing'|'blocked';error:GeometryFailure|null}>;
export type GeometrySnapshot=Readonly<{view:GeometryViewState|null;operation:GeometryOperation|null;retirement:GeometryRetirement|null;iconsSuspended:boolean}>;
export type GeometrySourceRoute=Readonly<{tableId:string;artifactId:string;rowId:string}>;
export const geometryIntentKey=(intent:GeometryIntent)=>geometrySelectionKey(intent.selection);

function freeze<T>(value:T):T{
 if(value&&typeof value==='object'){for(const child of Object.values(value))freeze(child);Object.freeze(value);}return value;
}
export function ownGeometryValue<T>(value:T):T{return freeze(structuredClone(value));}
function same(a:unknown,b:unknown):boolean{
 if(a===b)return true;if(!a||!b||typeof a!=='object'||typeof b!=='object'||Array.isArray(a)!==Array.isArray(b))return false;
 const x=a as Record<string,unknown>,y=b as Record<string,unknown>;
 return Object.keys(x).length===Object.keys(y).length&&Object.keys(x).every(k=>Object.hasOwn(y,k)&&same(x[k],y[k]));
}
/** Compact C2 facts only. Callers must pass current, accepted C2 data. No target snapshots are retained. */
export function captureGeometryAnchor(opening:OpeningSourceReference,metadata:CreatureGeometryMetadata):GeometryEntryAnchor{
 if(metadata.state!=='metadataResolved'||metadata.selected?.state!=='declared'||metadata.targets.length!==2||!metadata.modelFileDataId)throw new TypeError('Current resolved display metadata is required.');
 return ownGeometryValue({opening,metadata});
}
export function geometryAnchorFromDisplay(value:CreatureDisplayMetadataResult):GeometryEntryAnchor{
 if(!creatureDisplayMatches(value,{...value.context,artifactId:value.artifactId,creatureId:value.creatureId,slot:value.slot}))throw new TypeError('Current accepted display metadata is required.');
 const targets=value.sources.map(t=>({role:t.role,tableId:t.row?.source.tableId??(t.role==='display'?'bfdaf9f1':'7f695fa5'),artifactId:t.snapshot.artifactId,coverage:t.snapshot.coverage,matchCount:t.matchCount,row:t.row})) as CreatureGeometryMetadata['targets'];
 return captureGeometryAnchor(value.opening,{state:value.state,creatureSource:value.creatureSource,creatureCoverage:value.creatureCoverage,selected:value.selected,targets,absence:value.absence,modelId:value.modelId,modelFileDataId:value.modelFileDataId,requiredTableId:value.requiredTableId,appearance:value.appearance});
}
export function geometryAnchorMatches(anchor:GeometryEntryAnchor,result:CreatureGeometryResult):boolean{
 const actual=result.metadata,expected=anchor.metadata;
 if(!same(anchor.opening,result.opening)||!same(expected.creatureSource,actual.creatureSource)||!same(expected.creatureCoverage,actual.creatureCoverage)||!same(expected.selected,actual.selected))return false;
 if(actual.targets.length>expected.targets.length||actual.targets.some((target,index)=>!same(target,expected.targets[index])))return false;
 if(actual.modelId!==null&&actual.modelId!==expected.modelId||actual.modelFileDataId!==null&&actual.modelFileDataId!==expected.modelFileDataId)return false;
 return !['ready','noDeclaredTriangles'].includes(result.state)||actual.targets.length===2&&actual.modelId===expected.modelId&&actual.modelFileDataId===expected.modelFileDataId;
}
export function sameGeometryAnchor(a:GeometryEntryAnchor,b:GeometryEntryAnchor):boolean{return same(a,b);}
export function ownGeometryIntent(value:GeometryIntent):GeometryIntent{
 if(value.label.buildVersion.length>128||value.origin.key.length>2048||value.origin.innerScroll.length>4)throw new TypeError('Geometry presentation context exceeded its bound.');
 const name=value.label.name.length<=256?value.label.name:value.label.name.slice(0,254).replace(/[\uD800-\uDBFF]$/,'')+'…';
 return Object.freeze({selection:ownGeometryValue(value.selection),anchor:ownGeometryValue(value.anchor),label:ownGeometryValue({...value.label,name}),origin:Object.freeze({...value.origin,windowScroll:Object.freeze([...value.origin.windowScroll]) as readonly[number,number],innerScroll:Object.freeze(value.origin.innerScroll.map(s=>Object.freeze({...s})))})});
}
export function geometryAvailable(bridge:DiscoveryBridge,capabilities:readonly string[]):boolean{
 return ['creature-source-geometry-v1','creatures-v1','creature-display-metadata-v1'].every(c=>capabilities.includes(c))&&(['creatureGeometryRead','creatureGeometryPrepare','geometryViewEnter','geometryViewOpen','geometryViewBodyRetired','geometryViewSceneDisposed','geometryViewRelease','onGeometryRetirement'] as const).every(k=>typeof bridge[k]==='function');
}
export function geometryMeasurement(progress:Pick<EngineProgress,'completed'|'total'|'unit'>|null):Readonly<{percent:number|null;label:string}>|null{
 if(!progress||progress.completed===null)return null;
 const {completed,total}=progress;if(!Number.isSafeInteger(completed)||completed<0||total!==null&&(!Number.isSafeInteger(total)||total<completed))return null;
 return {percent:total!==null&&total>0?Math.floor(completed/total*100):null,label:total===null?`${completed.toLocaleString()} ${progress.unit??''}`:`${completed.toLocaleString()} of ${total.toLocaleString()} ${progress.unit??''}`};
}
export const geometryOutcome:Record<CreatureGeometryResult['state'],string>={ready:'Drawing geometry',notPrepared:'Required geometry is not prepared',metadataNotResolved:'Display metadata needs attention',noDeclaredTriangles:'Declared main-view sections reference no triangles',missingMembership:'Required source file is not in the admitted scope',ambiguousVariants:'Source variants disagree',encodingUnresolved:'Source encoding is unresolved',protected:'Required source is protected',unsupported:'This source geometry is unsupported',unavailable:'Required source is unavailable',corrupt:'Geometry failed integrity checks',resourceLimit:'Geometry exceeded a resource limit'};
/** Used only by deliberate Return/source navigation, never asynchronous completion. */
export function restoreGeometryOrigin(origin:GeometryReturnOrigin,currentOriginKey:string,fallback:HTMLElement|null,scroll:(x:number,y:number)=>void=(x,y)=>window.scrollTo(x,y)):boolean{
 const target=origin.invoker;
 if(origin.key===currentOriginKey&&target?.isConnected&&!target.closest('[hidden],[inert],[aria-hidden="true"]')&&target.dataset.geometryOrigin===origin.key){target.focus({preventScroll:true});scroll(...origin.windowScroll);for(const s of origin.innerScroll)if(s.element.isConnected&&!s.element.closest('[hidden],[inert]')){s.element.scrollLeft=s.left;s.element.scrollTop=s.top;}return true;}
 fallback?.focus({preventScroll:true});return false;
}
