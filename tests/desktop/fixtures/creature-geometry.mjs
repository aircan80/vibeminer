import template from '../../../contracts/examples/creature-geometry-ready.json' with{type:'json'};
import {createHash} from 'node:crypto';
// Authored VMG1 app-format bytes (an octahedron), not game data. Layout follows contracts/geometry-vmg1-v1.md.
const binding='5a'.repeat(32);
export function geometryArtifact(){
 const positions=[2,0,0,-2,0,0,0,3,0,0,-1,0,0,0,1.5,0,0,-1.5],triangles=[[0,2,4],[2,1,4],[1,3,4],[3,0,4],[2,0,5],[1,2,5],[3,1,5],[0,3,5]];
 const vertexCount=positions.length/3,lines=triangles.flatMap(([a,b,c])=>[a,b,b,c,c,a]),positionBytes=positions.length*4,lineOffset=128+positionBytes,sectionOffset=lineOffset+lines.length*4;
 const bytes=Buffer.alloc(sectionOffset+32);
 bytes.write('VMG1',0,'ascii');bytes.writeUInt16LE(1,4);bytes.writeUInt16LE(128,6);bytes.writeUInt32LE(bytes.length,8);bytes.writeUInt32LE(1,12);Buffer.from(binding,'hex').copy(bytes,16);
 for(const[offset,value]of[[48,vertexCount],[52,lines.length],[56,1],[60,vertexCount],[64,128],[68,positionBytes],[72,lineOffset],[76,lines.length*4],[80,sectionOffset],[84,32]])bytes.writeUInt32LE(value,offset);
 const min=[0,1,2].map(a=>Math.fround(Math.min(...positions.filter((_,i)=>i%3===a)))||0),max=[0,1,2].map(a=>Math.fround(Math.max(...positions.filter((_,i)=>i%3===a)))||0);
 min.forEach((v,i)=>bytes.writeFloatLE(v,88+i*4));max.forEach((v,i)=>bytes.writeFloatLE(v,100+i*4));
 bytes.writeUInt32LE(lines.length/2,112);bytes.writeUInt32LE(1,116);bytes.writeUInt32LE(triangles.length*3,120);bytes.writeUInt32LE(0,124);
 positions.forEach((v,i)=>bytes.writeFloatLE(v,128+i*4));lines.forEach((v,i)=>bytes.writeUInt32LE(v,lineOffset+i*4));
 // One section: ordinal, id, level, lookup start/count, triangle start/count, line start/count.
 bytes.writeUInt32LE(0,sectionOffset);bytes.writeUInt16LE(5,sectionOffset+4);bytes.writeUInt16LE(0,sectionOffset+6);
 for(const[offset,value]of[[8,0],[12,vertexCount],[16,0],[20,triangles.length*3],[24,0],[28,lines.length]])bytes.writeUInt32LE(value,sectionOffset+offset);
 const sha256=createHash('sha256').update(bytes).digest('hex');
 const manifest={...structuredClone(template.manifest),artifactId:createHash('sha256').update('authored-geometry-artifact').digest('hex'),bindingSha256:binding,sha256,byteLength:bytes.length,
  vertexCount,vertexLookupCount:vertexCount,sourceTriangleIndexCount:triangles.length*3,sectionIndexCount:triangles.length*3,lineIndexCount:lines.length,sectionCount:1,
  positions:{offset:128,byteLength:positionBytes},lineIndices:{offset:lineOffset,byteLength:lines.length*4},sections:{offset:sectionOffset,byteLength:32},bounds:{min,max}};
 return {bytes,manifest};
}
/** A geometry result over the exact display metadata the fixture returned for the same slot. */
export function creatureGeometry(display,state,manifest){
 const v=structuredClone(template),targets=display.sources.map(t=>({role:t.role,tableId:t.row?.source.tableId??(t.role==='display'?'bfdaf9f1':'7f695fa5'),artifactId:t.snapshot.artifactId,coverage:t.snapshot.coverage,matchCount:t.matchCount,row:t.row}));
 Object.assign(v,{context:display.context,artifactId:display.artifactId,creatureId:display.creatureId,slot:display.slot,opening:display.opening,state,problem:null,
  metadata:{state:display.state,creatureSource:display.creatureSource,creatureCoverage:display.creatureCoverage,selected:display.selected,targets,absence:display.absence,modelId:display.modelId,modelFileDataId:display.modelFileDataId,requiredTableId:display.requiredTableId,appearance:'notEvaluated'}});
 v.sources[0].fileDataId=display.modelFileDataId;
 v.manifest=state==='ready'?{...manifest,modelFileDataId:Number(display.modelFileDataId),skinFileDataId:Number(v.sources[1].fileDataId)}:null;
 if(!['ready','noDeclaredTriangles'].includes(state))v.sources=[];
 if(state==='notPrepared')v.problem=null;
 else if(!['ready','noDeclaredTriangles'].includes(state))v.problem={code:'AUTHORED_'+state.replace(/[A-Z]/g,c=>'_'+c).toUpperCase(),message:'Authored geometry outcome.',retryable:false,dependency:'skin',target:null};
 return v;
}
// Authored VMA1 textured appearance over the same octahedron: UV0 per vertex, one draw, one 4×4 RGBA8 checker. Not game data.
// Layout follows contracts/geometry-vma1-v1.md.
export function appearanceArtifact(geometry,displayId){
 const positions=[2,0,0,-2,0,0,0,3,0,0,-1,0,0,0,1.5,0,0,-1.5],uvs=[0,0.5,1,0.5,0.5,0,0.5,1,0.25,0.25,0.75,0.75],indices=[0,2,4,2,1,4,1,3,4,3,0,4,2,0,5,1,2,5,3,1,5,0,3,5];
 const v=positions.length/3,n=indices.length,texels=4*4*4,uvOffset=160+v*12,indexOffset=uvOffset+v*8,drawOffset=indexOffset+n*4,textureOffset=drawOffset+32,texelOffset=textureOffset+32;
 const bytes=Buffer.alloc(texelOffset+texels),bind='6b'.repeat(32);
 bytes.write('VMA1',0,'ascii');bytes.writeUInt16LE(1,4);bytes.writeUInt16LE(160,6);bytes.writeUInt32LE(bytes.length,8);bytes.writeUInt32LE(1,12);Buffer.from(bind,'hex').copy(bytes,16);
 const regions=[[160,v*12],[uvOffset,v*8],[indexOffset,n*4],[drawOffset,32],[textureOffset,32],[texelOffset,texels]];
 for(const[offset,value]of[[48,v],[52,n],[56,1],[60,1],[136,128],[140,displayId]])bytes.writeUInt32LE(value,offset);regions.forEach(([o,l],i)=>{bytes.writeUInt32LE(o,64+i*8);bytes.writeUInt32LE(l,68+i*8);});
 geometry.bounds.min.forEach((x,i)=>bytes.writeFloatLE(x,112+i*4));geometry.bounds.max.forEach((x,i)=>bytes.writeFloatLE(x,124+i*4));
 positions.forEach((x,i)=>bytes.writeFloatLE(x,160+i*4));uvs.forEach((x,i)=>bytes.writeFloatLE(x,uvOffset+i*4));indices.forEach((x,i)=>bytes.writeUInt32LE(x,indexOffset+i*4));
 bytes.writeUInt32LE(n,drawOffset+16);bytes.writeUInt32LE(2,drawOffset+28);
 for(const[offset,value]of[[4,779],[8,4],[12,4],[16,texelOffset],[20,texels]])bytes.writeUInt32LE(value,textureOffset+offset);bytes[textureOffset+25]=2;bytes[textureOffset+26]=3;bytes.writeUInt16LE(4,textureOffset+28);bytes.writeUInt16LE(4,textureOffset+30);
 for(let y=0;y<4;y++)for(let x=0;x<4;x++){const at=texelOffset+(y*4+x)*4,on=(x+y)%2===0;bytes[at]=on?200:30;bytes[at+1]=on?40:120;bytes[at+2]=on?40:200;bytes[at+3]=255;}
 const region=([offset,byteLength])=>({offset,byteLength});
 const manifest={artifactId:createHash('sha256').update('authored-appearance-artifact').digest('hex'),bindingSha256:bind,sha256:createHash('sha256').update(bytes).digest('hex'),byteLength:bytes.length,formatVersion:1,headerBytes:160,
  coordinatePolicy:'native-m2-position-normal-uv-winding-v1',vertexCount:v,indexCount:n,drawCount:1,textureCount:1,positions:region(regions[0]),uvs:region(regions[1]),indices:region(regions[2]),draws:region(regions[3]),textures:region(regions[4]),texels:region(regions[5]),
  bounds:structuredClone(geometry.bounds),alphaReference:128,displayId,geometryBindingSha256:geometry.bindingSha256,materialPolicy:'m2-single-texture-opaque-alphakey-v1',texturePolicy:'blp2-largest-mip-within-1024-rgba8-v1',
  readerRevision:'m2-appearance-batches-v1',decoderRevision:'blp2-rgba8-v2',artifactRevision:'model-appearance-vma1-v1'};
 const appearance={policy:'creature-textured-appearance-v1',state:'ready',display:{displayId:String(displayId),extendedDisplayInfoId:'0',textureVariations:['779','0','0','0']},
  textures:[{ordinal:0,type:11,binding:'variation',variation:0,fileDataId:'779',state:'decoded',image:{width:4,height:4,mipLevel:0,baseWidth:4,baseHeight:4,encoding:'bc1'},
   source:{contentKey:'a'.repeat(32),encodingKey:'b'.repeat(32),encodedSha256:'c'.repeat(64),encodedBytes:120,decodedBytes:180,fullVerifierRevision:'content-blte-v1'}}],
  effects:{particles:2,ribbons:0,lights:0,shadowBatches:1,unbatchedSections:0,evaluation:'notDrawn'},manifest,problem:null};
 return {bytes,manifest,appearance};
}
