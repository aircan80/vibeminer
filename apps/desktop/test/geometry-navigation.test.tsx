import {cleanup,fireEvent,render,screen} from '@testing-library/react';
import {afterEach,expect,it,vi} from 'vitest';
import '@testing-library/jest-dom/vitest';
import ready from '../../../contracts/examples/creature-geometry-ready.json';
import type {CreatureGeometryResult} from '../../../contracts/generated/discovery';
import {CreatureGeometryView} from '../src/renderer/CreatureGeometryView';
import type {GeometryFlow} from '../src/renderer/useCreatureGeometry';
import {captureGeometryAnchor,restoreGeometryOrigin,type GeometryViewState} from '../src/renderer/geometry-state';
const result=ready as CreatureGeometryResult;
function flow(){const intent={selection:{...result.context,artifactId:result.artifactId,creatureId:result.creatureId,slot:result.slot},anchor:captureGeometryAnchor(result.opening,result.metadata),label:{name:'Authored',buildVersion:'Authored'},origin:{key:'exact',invoker:null,windowScroll:[0,0] as const,innerScroll:[]}};const view:GeometryViewState={intent,generation:1,phase:'ready',message:'Unposed source edges drawn',result,canMount:true,error:null,sections:[],sectionOffset:0};return{session:{attachCanvas:vi.fn(()=>vi.fn()),move:vi.fn(),sectionPage:vi.fn(),stopPreparation:vi.fn(),retry:vi.fn()},snapshot:{view,operation:null,retirement:null,iconsSuspended:true}} as unknown as GeometryFlow;}
afterEach(cleanup);
it('focuses deliberate entry once and never steals focus on later progress/result renders',()=>{
 const value=flow(),back=vi.fn(),{rerender}=render(<><button>Other source control</button><CreatureGeometryView flow={value} onReturn={back}/></>);expect(screen.getByRole('heading',{name:'Diagnostic source geometry'})).toHaveFocus();const outside=screen.getByRole('button',{name:'Other source control'});outside.focus();rerender(<><button>Other source control</button><CreatureGeometryView flow={{...value,snapshot:{...value.snapshot,view:{...value.snapshot.view!,message:'A later source stage'}}}} onReturn={back}/></>);expect(outside).toHaveFocus();fireEvent.click(screen.getByRole('button',{name:'Return to selected display metadata'}));expect(back).toHaveBeenCalledTimes(1);
});
it.each(['canvas','Rotate left','Rotate right','Rotate up','Rotate down','Zoom in','Zoom out','Reset view'])('moves focus from removed %s to Return after actual context loss, preserving outside focus',control=>{
 const value=flow(),{rerender,container}=render(<><button>Outside</button><CreatureGeometryView flow={value} onReturn={()=>{}}/></>);const old=control==='canvas'?container.querySelector('canvas')!:screen.getByRole('button',{name:control});old.focus();
 rerender(<><button>Outside</button><CreatureGeometryView flow={{...value,snapshot:{...value.snapshot,view:{...value.snapshot.view!,phase:'outcome',message:'The graphics context was lost',canMount:false,error:{code:'GEOMETRY_CONTEXTLOST',message:'Authored loss',retryable:true}}}}} onReturn={()=>{}}/></>);
 expect(screen.getByRole('button',{name:'Return to selected display metadata'})).toHaveFocus();
 const outside=screen.getByRole('button',{name:'Outside'});outside.focus();rerender(<><button>Outside</button><CreatureGeometryView flow={{...value,snapshot:{...value.snapshot,view:{...value.snapshot.view!,phase:'blocked',canMount:false,message:'Still closing'}}}} onReturn={()=>{}}/></>);expect(outside).toHaveFocus();
});
it('refuses focus and scroll restoration into hidden cached metadata even with a matching reused DOM key',()=>{
 const hidden=document.createElement('div'),button=document.createElement('button'),fallback=document.createElement('h3');hidden.hidden=true;button.dataset.geometryOrigin='same';hidden.append(button);fallback.tabIndex=-1;document.body.append(hidden,fallback);const scroll=vi.fn();expect(restoreGeometryOrigin({key:'same',invoker:button,windowScroll:[12,34],innerScroll:[]},'same',fallback,scroll)).toBe(false);expect(fallback).toHaveFocus();expect(scroll).not.toHaveBeenCalled();hidden.remove();fallback.remove();
});
it('focuses a deliberate same-key reentry after a mounted null route, then ignores async phase changes',()=>{
 const value=flow(),{rerender}=render(<><button>Outside</button><CreatureGeometryView flow={value} onReturn={()=>{}}/></>);rerender(<><button>Outside</button><CreatureGeometryView flow={{...value,snapshot:{...value.snapshot,view:null}}} onReturn={()=>{}}/></>);screen.getByRole('button',{name:'Outside'}).focus();const intent={...value.snapshot.view!.intent},next={...value.snapshot.view!,intent,generation:2,phase:'entering' as const,canMount:false};rerender(<><button>Outside</button><CreatureGeometryView flow={{...value,snapshot:{...value.snapshot,view:next}}} onReturn={()=>{}}/></>);expect(screen.getByRole('heading',{name:'Diagnostic source geometry'})).toHaveFocus();screen.getByRole('button',{name:'Outside'}).focus();rerender(<><button>Outside</button><CreatureGeometryView flow={{...value,snapshot:{...value.snapshot,view:{...next,phase:'ready',canMount:true}}}} onReturn={()=>{}}/></>);expect(screen.getByRole('button',{name:'Outside'})).toHaveFocus();
});
it('scrolls the view top into place on deliberate entry only, never on later renders (#59)',()=>{
 const scroll=vi.fn();Element.prototype.scrollIntoView=scroll;try{
  const value=flow(),{rerender}=render(<CreatureGeometryView flow={value} onReturn={()=>{}}/>);
  expect(scroll).toHaveBeenCalledTimes(1);expect(scroll).toHaveBeenCalledWith({block:'start'});expect(scroll.mock.contexts[0]).toBe(document.querySelector('.creature-geometry'));
  rerender(<CreatureGeometryView flow={{...value,snapshot:{...value.snapshot,view:{...value.snapshot.view!,message:'A later source stage'}}}} onReturn={()=>{}}/>);expect(scroll).toHaveBeenCalledTimes(1);
 }finally{delete (Element.prototype as Partial<Element>).scrollIntoView;}
});
