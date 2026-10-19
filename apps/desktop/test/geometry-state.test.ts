import {describe,expect,it} from 'vitest';
import ready from '../../../contracts/examples/creature-geometry-ready.json';
import type {CreatureGeometryResult} from '../../../contracts/generated/discovery';
import {captureGeometryAnchor,geometryAnchorMatches,geometryAvailable,geometryMeasurement,restoreGeometryOrigin} from '../src/renderer/geometry-state';

const result=()=>structuredClone(ready) as CreatureGeometryResult;
describe('geometry source presentation protection',()=>{
 it('owns compact exact opening/selection/ordered target evidence without retaining caller graphs',()=>{
  const value=result(), anchor=captureGeometryAnchor(value.opening,value.metadata);
  value.metadata.targets[0]!.row!.copySourceId='20';
  expect(anchor.metadata.targets[0]!.row!.copySourceId).toBe('11');
  expect(Object.isFrozen(anchor.metadata.targets)).toBe(true);
  expect(geometryAnchorMatches(anchor,result())).toBe(true);
 });
 it.each(['opening','probability','copy','scope','artifact','edge'])('refuses changed %s despite the same model FDID',kind=>{
  const original=result(),anchor=captureGeometryAnchor(original.opening,original.metadata),changed=result();
  if(kind==='opening')changed.opening.generation='2';
  if(kind==='probability')changed.metadata.selected!.probability='0.5';
  if(kind==='copy')changed.metadata.targets[0]!.row!.copySourceId='12';
  if(kind==='scope')changed.metadata.targets[0]!.coverage.physicalRows='3';
  if(kind==='artifact')changed.metadata.targets[1]!.artifactId='a'.repeat(64);
  if(kind==='edge')changed.metadata.targets[1]!.row!.edgeValue.columnIndex=1;
  expect(geometryAnchorMatches(anchor,changed)).toBe(false);
 });
 it('allows only a consistent undelivered prerequisite suffix, never a changed reached source',()=>{
  const value=result(),anchor=captureGeometryAnchor(value.opening,value.metadata);
  value.state='notPrepared';value.manifest=null;value.sources=[];value.metadata.state='notPrepared';value.metadata.targets=[value.metadata.targets[0]!];value.metadata.modelFileDataId=null;value.metadata.requiredTableId='7f695fa5';
  expect(geometryAnchorMatches(anchor,value)).toBe(true);
  value.metadata.targets[0]!.row!.copySourceId='12';expect(geometryAnchorMatches(anchor,value)).toBe(false);
 });
 it('does not expose geometry when any real prerequisite bridge method/event/capability is missing',()=>{
  const names=['creatureGeometryRead','creatureGeometryPrepare','geometryViewEnter','geometryViewOpen','geometryViewBodyRetired','geometryViewSceneDisposed','geometryViewRelease','onGeometryRetirement'];
  const bridge=Object.fromEntries(names.map(name=>[name,()=>{}]));
  const caps=['creature-source-geometry-v1','creatures-v1','creature-display-metadata-v1'];
  expect(geometryAvailable(bridge as never,caps)).toBe(true);
  for(const name of names)expect(geometryAvailable({...bridge,[name]:undefined} as never,caps)).toBe(false);
  expect(geometryAvailable(bridge as never,caps.slice(1))).toBe(false);
 });
 it('reports measured current-stage percent only for a real known positive denominator',()=>{
  expect(geometryMeasurement({completed:3,total:4,unit:'entries'})).toEqual({percent:75,label:'3 of 4 entries'});
  expect(geometryMeasurement({completed:3,total:null,unit:'bytes'})?.percent).toBeNull();
  expect(geometryMeasurement({completed:0,total:0,unit:'entries'})?.percent).toBeNull();
 });
 it('restores only a current exact connected invoker and its saved scroll, with stale fallback',()=>{
  const invoker=document.createElement('button'),fallback=document.createElement('h3');fallback.tabIndex=-1;
  invoker.dataset.geometryOrigin='exact';document.body.append(invoker,fallback);
  const origin={key:'exact',invoker,windowScroll:[2,3] as const,innerScroll:[]};
  const scrolls:number[][]=[];const scroll=(x:number,y:number)=>{scrolls.push([x,y]);};
  expect(restoreGeometryOrigin(origin,'exact',fallback,scroll)).toBe(true);expect(document.activeElement).toBe(invoker);expect(scrolls).toEqual([[2,3]]);
  invoker.dataset.geometryOrigin='reused';expect(restoreGeometryOrigin(origin,'exact',fallback,scroll)).toBe(false);expect(document.activeElement).toBe(fallback);expect(scrolls).toHaveLength(1);
  invoker.remove();fallback.remove();
 });
});
