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
