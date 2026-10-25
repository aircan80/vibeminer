import {afterEach,expect,it,vi} from 'vitest';
import {cleanup,fireEvent,render,screen,waitFor} from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import readyJson from '../../../contracts/examples/creature-geometry-ready.json';
import texturedJson from '../../../contracts/examples/creature-geometry-textured.json';
import type {CreatureGeometryResult} from '../../../contracts/generated/discovery';
import {CreatureGeometryView} from '../src/renderer/CreatureGeometryView';
import {CreatureGeometryActivity} from '../src/renderer/CreatureGeometryActivity';
import {captureGeometryAnchor,type GeometryViewState} from '../src/renderer/geometry-state';
import type {GeometryFlow} from '../src/renderer/useCreatureGeometry';
const result=readyJson as CreatureGeometryResult;
function fixture(phase:GeometryViewState['phase']='ready'){
 const intent={selection:{...result.context,artifactId:result.artifactId,creatureId:result.creatureId,slot:result.slot},anchor:captureGeometryAnchor(result.opening,result.metadata),label:{name:'Authored Creature',buildVersion:'Authored build'},origin:{key:'exact',invoker:null,windowScroll:[0,0] as const,innerScroll:[]}};
 const view:GeometryViewState={intent,generation:1,phase,message:phase==='ready'?'Unposed source edges drawn':'Drawing geometry',result,canMount:true,error:null,sections:[],sectionOffset:0};
 const session={attachCanvas:vi.fn(()=>vi.fn()),move:vi.fn(),sectionPage:vi.fn(),retry:vi.fn(),stopPreparation:vi.fn(),dismissActivity:vi.fn()};
 return{flow:{session,snapshot:{view,operation:null,retirement:null,iconsSuspended:true}} as unknown as GeometryFlow,session,view};
}
afterEach(cleanup);
it('commits the sole real canvas in Drawing before drawn without exposing premature controls',()=>{
 const {flow,session}=fixture('validating'),onReturn=vi.fn();const {rerender,container}=render(<CreatureGeometryView flow={flow} onReturn={onReturn}/>);
 expect(container.querySelectorAll('canvas')).toHaveLength(1);expect(session.attachCanvas).toHaveBeenCalledTimes(1);expect(screen.getByRole('status')).toHaveTextContent('Drawing geometry');expect(screen.queryByRole('group',{name:'Geometry camera'})).not.toBeInTheDocument();
 rerender(<CreatureGeometryView flow={{...flow,snapshot:{...flow.snapshot,view:{...flow.snapshot.view!,phase:'ready'}}}} onReturn={onReturn}/>);
 fireEvent.click(screen.getByRole('button',{name:'Rotate left'}));expect(session.move).toHaveBeenCalledWith({kind:'rotate',yaw:-Math.PI/12,elevation:0});expect(session.attachCanvas).toHaveBeenCalledTimes(1);expect(screen.queryByRole('button',{name:/Stop/})).not.toBeInTheDocument();
});
it('displays scoped original facts and working exact raw-record callbacks without model-path controls',()=>{
 const {flow}=fixture(),onSource=vi.fn();render(<CreatureGeometryView flow={flow} onReturn={()=>{}} onSource={onSource}/>);
 expect(screen.getByText('Unposed source edges · appearance not evaluated')).toBeVisible();expect(screen.getByText('All declared main-view sections')).toBeVisible();expect(screen.getByText('Copy target 23 from 11 · column 0 = 19')).toBeVisible();
 fireEvent.click(screen.getByRole('button',{name:'View CreatureDisplayInfo source record'}));expect(onSource).toHaveBeenCalledWith(result.metadata.targets[0]!.row!.source);
 expect(screen.queryByRole('button',{name:/export|texture|portrait|skin source/i})).not.toBeInTheDocument();
 expect(screen.getByText('Section triangle references').nextElementSibling).toHaveTextContent('2');expect(screen.getByText('Emitted edge pairs').nextElementSibling).toHaveTextContent('6');
});
it('pages only bounded section evidence and never remounts or changes all-section drawing',async()=>{
 const {flow,session}=fixture(),rows=Array.from({length:16},(_,ordinal)=>({ordinal,id:ordinal+50,level:0,vertexStart:0,vertexCount:3,triangleStart:0,triangleCount:3,lineStart:ordinal*6,lineCount:6}));
 const extended={...result,manifest:{...result.manifest!,sectionCount:33}};
 const {rerender,container}=render(<CreatureGeometryView flow={{...flow,snapshot:{...flow.snapshot,view:{...flow.snapshot.view!,result:extended}}}} onReturn={()=>{}}/>);
 fireEvent.click(screen.getByText('Declared section evidence'));await waitFor(()=>expect(session.sectionPage).toHaveBeenCalledWith(0));
 rerender(<CreatureGeometryView flow={{...flow,snapshot:{...flow.snapshot,view:{...flow.snapshot.view!,result:extended,sections:rows}}}} onReturn={()=>{}}/>);
 expect(container.querySelectorAll('tbody tr')).toHaveLength(16);fireEvent.click(screen.getByRole('button',{name:'Next section records'}));expect(session.sectionPage).toHaveBeenLastCalledWith(16);expect(session.attachCanvas).toHaveBeenCalledTimes(1);expect(session.move).not.toHaveBeenCalled();
});
it('gives Q0 and empty-source failure useful metadata without fabricated manifest or verified asset facts',()=>{
 const {flow}=fixture('outcome');const q0={...result,state:'noDeclaredTriangles' as const,manifest:null};const {rerender,container}=render(<CreatureGeometryView flow={{...flow,snapshot:{...flow.snapshot,view:{...flow.snapshot.view!,result:q0,message:'Declared main-view sections reference no triangles',canMount:false}}}} onReturn={()=>{}}/>);
 expect(container.querySelector('canvas')).toBeNull();expect(screen.queryByText('Declared section evidence')).not.toBeInTheDocument();expect(screen.queryByText('Declared geometry')).not.toBeInTheDocument();expect(screen.getByText('Model · full verified source')).toBeVisible();
 rerender(<CreatureGeometryView flow={{...flow,snapshot:{...flow.snapshot,view:{...flow.snapshot.view!,result:{...q0,state:'protected',sources:[],problem:{code:'PROTECTED',message:'Authored protected source',retryable:false,dependency:'skin',target:null}},canMount:false}}}} onReturn={()=>{}}/>);
 expect(screen.queryByText(/full verified source/)).not.toBeInTheDocument();expect(screen.getByText('Model FileDataID · source value')).toBeVisible();
});
it('keeps original-context closing/blocked activity and no misleading dismiss or retry through debt',()=>{
 const {flow,view}=fixture();render(<CreatureGeometryActivity flow={{...flow,snapshot:{...flow.snapshot,view:null,retirement:{intent:view.intent,phase:'blocked',error:{code:'CLEANUP_FAILED',message:'Authored uncertain close',retryable:false}}}}}/>);
 expect(screen.getByRole('region',{name:'Geometry activity'})).toHaveTextContent('wow · build Authored build · source US');expect(screen.getByText('Geometry cleanup needs attention')).toBeVisible();expect(screen.getByText(/Icons are paused/)).toBeVisible();expect(screen.queryByRole('button')).not.toBeInTheDocument();
});
it('does not offer empty early section inspection, then preserves the focused pager at both boundaries',async()=>{
 const {flow,session}=fixture('validating'),extended={...result,manifest:{...result.manifest!,sectionCount:33}},initial={...flow,snapshot:{...flow.snapshot,view:{...flow.snapshot.view!,result:extended}}};
 const {rerender}=render(<CreatureGeometryView flow={initial} onReturn={()=>{}}/>);expect(screen.queryByText('Declared section evidence')).not.toBeInTheDocument();
 const readyFlow={...initial,snapshot:{...initial.snapshot,view:{...initial.snapshot.view!,phase:'ready' as const}}};rerender(<CreatureGeometryView flow={readyFlow} onReturn={()=>{}}/>);fireEvent.click(screen.getByText('Declared section evidence'));await waitFor(()=>expect(session.sectionPage).toHaveBeenCalledWith(0));
 const next=screen.getByRole('button',{name:'Next section records'});next.focus();rerender(<CreatureGeometryView flow={{...readyFlow,snapshot:{...readyFlow.snapshot,view:{...readyFlow.snapshot.view!,sectionOffset:32,sections:[{ordinal:32,id:5,level:0,vertexStart:0,vertexCount:3,triangleStart:0,triangleCount:3,lineStart:0,lineCount:6}]}}}} onReturn={()=>{}}/>);expect(next).toHaveFocus();expect(next).toHaveAttribute('aria-disabled','true');session.sectionPage.mockClear();fireEvent.click(next);expect(session.sectionPage).not.toHaveBeenCalled();
 const previous=screen.getByRole('button',{name:'Previous section records'});previous.focus();rerender(<CreatureGeometryView flow={readyFlow} onReturn={()=>{}}/>);expect(previous).toHaveFocus();expect(previous).toHaveAttribute('aria-disabled','true');fireEvent.click(previous);expect(session.sectionPage).not.toHaveBeenCalled();
});

it('shows the texture chain for a textured draw and names a refusal while the edges stay drawn',()=>{
 const textured=texturedJson as unknown as CreatureGeometryResult,{flow}=fixture();
 const view={...flow.snapshot.view!,result:textured,message:'Unposed textured surfaces drawn'};
 const {rerender}=render(<CreatureGeometryView flow={{...flow,snapshot:{...flow.snapshot,view}}} onReturn={()=>{}}/>);
 expect(screen.getByText('Unposed textured surfaces · bind pose, unlit')).toBeVisible();
 expect(screen.getByRole('region',{name:'Texture chain'})).toHaveTextContent('Texture 0 · type 11 → TextureVariationFileDataID[0] → FileDataID 779 · BC1, mip 0 2×2 of 2×2');
 expect(screen.getByRole('region',{name:'Texture chain'})).toHaveTextContent('Texture 1 · type 0 → TXID[1] → FileDataID 900 · BC1, mip 0 2×2 of 2×2');
 expect(screen.getByText(/Not drawn: 4 particle emitters, 0 ribbons, 0 lights, 2 shadow batches, 1 blended layer\. 1 env-map stage drawn without its UV animation\./)).toBeVisible();
 // #69 nit from #63: each label keeps its number on the same line.
 const rows=[...screen.getByRole('region',{name:'Texture chain'}).querySelectorAll('li')].map(li=>li.textContent);
 for(const pair of ['Texture\u00a00','type\u00a011','FileDataID\u00a0779','mip\u00a00','Texture\u00a01','type\u00a00','FileDataID\u00a0900'])expect(rows.join('\n')).toContain(pair);
 expect(rows.join('\n')).not.toMatch(/(Texture|type|FileDataID|mip) \d/);
 // ...and no arrow can start a line.
 for(const row of rows)expect(row!.match(/\u00a0→ /g)).toHaveLength(2);expect(rows.join('\n')).not.toMatch(/ →/);
 const refused={...textured,appearance:{...textured.appearance!,state:'multiTexture' as const,textures:[],manifest:null,problem:{code:'CREATURE_APPEARANCE_MULTI_TEXTURE',message:'The batch combines more than one texture stage.',retryable:false,batchOrdinal:3,textureOrdinal:null}}};
 rerender(<CreatureGeometryView flow={{...flow,snapshot:{...flow.snapshot,view:{...view,result:refused}}}} onReturn={()=>{}}/>);
 expect(screen.getByText('Unposed source edges · a batch combines more than two textures')).toBeVisible();
 expect(screen.getByRole('region',{name:'Texture chain'})).toHaveTextContent('CREATURE_APPEARANCE_MULTI_TEXTURE: The batch combines more than one texture stage. Batch 3.');
 const combiner={...refused,appearance:{...refused.appearance,state:'combinerUnsupported' as const,problem:{code:'CREATURE_APPEARANCE_COMBINER_UNSUPPORTED',message:'The batch combines its textures with Combiners_Mod_Mod with Diffuse_T1_T2 (shader 0x4011, 2 textures), which is not drawn.',retryable:false,batchOrdinal:0,textureOrdinal:null}}};
 rerender(<CreatureGeometryView flow={{...flow,snapshot:{...flow.snapshot,view:{...view,result:combiner}}}} onReturn={()=>{}}/>);
 expect(screen.getByText('Unposed source edges · a batch combines its textures in a way that is not drawn')).toBeVisible();
 expect(screen.getByRole('region',{name:'Texture chain'})).toHaveTextContent('Combiners_Mod_Mod with Diffuse_T1_T2 (shader 0x4011, 2 textures), which is not drawn. Batch 0.');
});

it('names the standard ROOT variant a texture used and the high-resolution one it left out (D095)',()=>{
 const textured=texturedJson as unknown as CreatureGeometryResult,{flow}=fixture(),texture=textured.appearance!.textures[0]!;
 const variant={policy:'root-texture-standard-resolution-v1' as const,contentKey:texture.source!.contentKey,contentFlags:0x2080000,highResolutionContentKey:'d'.repeat(32),highResolutionContentFlags:0x2080001};
 const result={...textured,appearance:{...textured.appearance!,textures:[{...texture,variant}]}};
 render(<CreatureGeometryView flow={{...flow,snapshot:{...flow.snapshot,view:{...flow.snapshot.view!,result}}}} onReturn={()=>{}}/>);
 expect(screen.getByRole('region',{name:'Texture chain'})).toHaveTextContent('FileDataID 779 · standard ROOT variant (content flags 0x02080000); high-resolution 0x02080001 not used · BC1, mip 0 2×2 of 2×2');
});

it('names texture chain states in plain words, counts in the singular and offers Retry textures for a retryable texture outcome',()=>{
 const textured=texturedJson as unknown as CreatureGeometryResult,{flow,session}=fixture();
 const appearance={...textured.appearance!,state:'textureUnavailable' as const,manifest:null,effects:{...textured.appearance!.effects!,ribbons:1,shadowBatches:1},
  textures:[{...textured.appearance!.textures[0]!,state:'unavailable' as const,image:null,source:null},{...textured.appearance!.textures[0]!,ordinal:1,state:'notReached' as const,image:null,source:null}],
  problem:{code:'CREATURE_APPEARANCE_TEXTURE_UNAVAILABLE',message:'Texture ordinal 0 source storage is unavailable.',retryable:true,batchOrdinal:null,textureOrdinal:0}};
 const retryAppearance=vi.fn();Object.assign(session,{retryAppearance});
 render(<CreatureGeometryView flow={{...flow,snapshot:{...flow.snapshot,view:{...flow.snapshot.view!,result:{...textured,appearance}}}}} onReturn={()=>{}}/>);
 const chain=screen.getByRole('region',{name:'Texture chain'});
 expect(chain).toHaveTextContent('FileDataID 779 · unavailable');expect(chain).toHaveTextContent('· not reached');expect(chain).not.toHaveTextContent('notReached');
 expect(chain).toHaveTextContent('Not drawn: 4 particle emitters, 1 ribbon, 0 lights, 1 shadow batch, 1 blended layer. 1 env-map stage drawn without its UV animation.');
 const plain={...textured,appearance:{...appearance,effects:{...appearance.effects,blendedBatches:0,envTransforms:0}}};
 cleanup();render(<CreatureGeometryView flow={{...flow,snapshot:{...flow.snapshot,view:{...flow.snapshot.view!,result:plain}}}} onReturn={()=>{}}/>);
 expect(screen.getByRole('region',{name:'Texture chain'})).toHaveTextContent('Not drawn: 4 particle emitters, 1 ribbon, 0 lights, 1 shadow batch. Geosets and colour animation are not evaluated.');
 cleanup();render(<CreatureGeometryView flow={{...flow,snapshot:{...flow.snapshot,view:{...flow.snapshot.view!,result:{...textured,appearance}}}}} onReturn={()=>{}}/>);
 fireEvent.click(screen.getByRole('button',{name:'Retry textures'}));expect(retryAppearance).toHaveBeenCalledTimes(1);
 expect(screen.queryByRole('button',{name:'Retry geometry'})).not.toBeInTheDocument();
});

it('shows an unresolved zero FileDataID once, as its state',()=>{
 const textured=texturedJson as unknown as CreatureGeometryResult,{flow}=fixture();
 const appearance={...textured.appearance!,state:'textureUnresolved' as const,manifest:null,textures:[{...textured.appearance!.textures[0]!,fileDataId:null,state:'unresolved' as const,image:null,source:null}],
  problem:{code:'CREATURE_APPEARANCE_TEXTURE_UNRESOLVED',message:'Texture ordinal 0 has a zero FileDataID.',retryable:false,batchOrdinal:null,textureOrdinal:0}};
 render(<CreatureGeometryView flow={{...flow,snapshot:{...flow.snapshot,view:{...flow.snapshot.view!,result:{...textured,appearance}}}}} onReturn={()=>{}}/>);
 const row=screen.getByRole('region',{name:'Texture chain'}).querySelector('li')!;
 expect(row).toHaveTextContent('Texture 0 · type 11 → TextureVariationFileDataID[0] → FileDataID 0 · unresolved');expect(row.textContent!.match(/unresolved/g)).toHaveLength(1);
});
