// Reads the authored engine's request log (engine.mjs appends one JSON line per request to requests.ndjson). A spec
// can read it while the engine is mid-append, so a last line with no newline yet is skipped as not written; any
// complete line that is not JSON still throws (#142).
import {readFile} from 'node:fs/promises';
import path from 'node:path';

export function parseNdjson(text){
  const lines=text.split('\n');
  lines.pop(); // '' after the final newline, or a line still being written
  return lines.map((line,index)=>{try{return JSON.parse(line);}catch(error){throw new Error(`ndjson line ${index+1} is not JSON: ${JSON.stringify(line.slice(0,200))}`,{cause:error});}});
}

export async function readRequests(directory){
  return parseNdjson(await readFile(path.join(directory,'requests.ndjson'),'utf8'));
}
