import type {ItemsContext,ItemIconBatch,ItemIconLeaseDescriptor,ItemIconOutcome} from '../../../../contracts/generated/discovery';
import {validItemsContext,sameItemsContext} from './items';
import {validRecordId} from './table-data';
export type IconBatchParams=ItemsContext&{artifactId:string;itemIds:string[]};
export type IconOpenParams=ItemsContext&{artifactId:string;itemId:string;imageArtifactId:string};
export type IconOutcome=Omit<ItemIconOutcome,'image'>&{image:(Omit<NonNullable<ItemIconOutcome['image']>,'artifactId'>&{url:string;requestLifetimeMs:number})|null};
export type IconBatch={context:ItemsContext;artifactId:string;outcomes:IconOutcome[]};
const hash=/^[0-9a-f]{64}$/,lease=/^[0-9a-f]{32}$/;
const fields=['productId','buildIdentity','sourceRegion','researchLocale'];
const context=(p:ItemsContext)=>validItemsContext(Object.fromEntries(fields.map(k=>[k,p[k as keyof ItemsContext]])));
export function validIconBatch(value:unknown,max=50):value is IconBatchParams{if(!value||typeof value!=='object')return false;const p=value as IconBatchParams;return context(p)&&typeof p.artifactId==='string'&&hash.test(p.artifactId)&&Array.isArray(p.itemIds)&&p.itemIds.length>=1&&p.itemIds.length<=max&&p.itemIds.every(id=>typeof id==='string'&&validRecordId(id))&&Object.keys(p).length===6&&Object.keys(p).every(k=>[...fields,'artifactId','itemIds'].includes(k));}
export function validIconOpen(value:unknown):value is IconOpenParams{if(!value||typeof value!=='object')return false;const p=value as IconOpenParams;return context(p)&&typeof p.artifactId==='string'&&hash.test(p.artifactId)&&typeof p.imageArtifactId==='string'&&hash.test(p.imageArtifactId)&&typeof p.itemId==='string'&&validRecordId(p.itemId)&&Object.keys(p).length===7&&Object.keys(p).every(k=>[...fields,'artifactId','itemId','imageArtifactId'].includes(k));}
export function validIconClose(value:unknown):value is {leaseId:string}{return !!value&&typeof value==='object'&&Object.keys(value).length===1&&typeof (value as{leaseId:unknown}).leaseId==='string'&&lease.test((value as{leaseId:string}).leaseId);}
export function iconBatchMatches(value:ItemIconBatch,p:IconBatchParams):boolean{return sameItemsContext(value.context,p)&&value.artifactId===p.artifactId&&value.outcomes.length===p.itemIds.length&&value.outcomes.every((o,i)=>{if(o.itemId!==p.itemIds[i])return false;if(o.status==='ready')return !!o.fileDataId&&validRecordId(o.fileDataId)&&o.fileDataId!=='0'&&!!o.image&&validImage(o.image)&&o.problem===null;return o.image===null;});}
export function validImage(image:{artifactId:string;byteLength:string;width:number;height:number}):boolean{return typeof image.artifactId==='string'&&hash.test(image.artifactId)&&typeof image.byteLength==='string'&&/^[1-9][0-9]{0,6}$/.test(image.byteLength)&&BigInt(image.byteLength)>=8n&&BigInt(image.byteLength)<=5242880n&&Number.isInteger(image.width)&&image.width>=1&&image.width<=1024&&Number.isInteger(image.height)&&image.height>=1&&image.height<=1024&&image.width*image.height<=1048576;}
export function iconLeaseMatches(value:ItemIconLeaseDescriptor,p:IconOpenParams):boolean{return typeof value.leaseId==='string'&&lease.test(value.leaseId)&&value.artifactId===p.imageArtifactId&&typeof value.sha256==='string'&&hash.test(value.sha256)&&validImage(value);}
export function validIconView(value:unknown):value is string{return typeof value==='string'&&/^[a-zA-Z0-9_-]{1,96}$/.test(value);}

const exact=(value:object,keys:string[])=>Object.keys(value).length===keys.length&&Object.keys(value).every(k=>keys.includes(k));
export function iconDeliveryMatches(value:unknown,p:IconBatchParams):value is IconBatch{
 const v=value as IconBatch;
 if(!v||!exact(v,['context','artifactId','outcomes'])||!validItemsContext(v.context)||!sameItemsContext(v.context,p)||v.artifactId!==p.artifactId||!Array.isArray(v.outcomes)||v.outcomes.length!==p.itemIds.length)return false;
 const statuses=['ready','missingItem','fieldUnavailable','noIcon','missingMembership','ambiguousVariants','encodingUnresolved','protected','unsupported','unavailable','corrupt'];
 return v.outcomes.every((o,i)=>{if(!o||!exact(o,['itemId','fileDataId','status','image','problem'])||o.itemId!==p.itemIds[i]||!statuses.includes(o.status)||o.fileDataId!==null&&(typeof o.fileDataId!=='string'||!validRecordId(o.fileDataId)))return false;
  if(o.problem!==null&&(!o.problem||!exact(o.problem,['code','message','retryable'])||typeof o.problem.code!=='string'||!o.problem.code.length||o.problem.code.length>128||typeof o.problem.message!=='string'||!o.problem.message.length||o.problem.message.length>1024||typeof o.problem.retryable!=='boolean'))return false;
  return o.status==='ready'?!!o.fileDataId&&o.fileDataId!=='0'&&!!o.image&&exact(o.image,['url','width','height','byteLength','requestLifetimeMs'])&&Number.isInteger(o.image.requestLifetimeMs)&&o.image.requestLifetimeMs>=1&&o.image.requestLifetimeMs<=1500000&&typeof o.image.url==='string'&&/^vibeminer-image:\/\/icon\/[0-9a-f]{32}$/.test(o.image.url)&&validImage({...o.image,artifactId:'a'.repeat(64)})&&o.problem===null:o.image===null;
 });
}
