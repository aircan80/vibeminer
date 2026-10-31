import {execFile} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {existsSync,mkdirSync,readFileSync,renameSync,writeFileSync,openSync,fsyncSync,closeSync,unlinkSync} from 'node:fs';
import path from 'node:path';

/**
 * Where the engine keeps its data (D128). `VIBEMINER_DATA_DIR` wins (tests and QA), then the folder the user chose, then the
 * per-OS default for big local data that isn't roaming or backed up: Windows %LOCALAPPDATA%, macOS Application Support with a
 * Time Machine exclusion, Linux $XDG_DATA_HOME or ~/.local/share. The choice lives in Electron's own userData folder, so it
 * survives whatever happens to the data folder itself.
 */
export type DataFolderSource='override'|'chosen'|'default';
export interface DataFolder{path:string;source:DataFolderSource}
/** `file` is the setting file or the folder at fault; `remedy` is the user's step. */
export interface DataFolderProblem{problem:string;file:string;remedy:string}
export interface DataFolderEnvironment{platform:NodeJS.Platform;env:Readonly<Record<string,string|undefined>>;home:string}

export const DATA_FOLDER_NAME='VibeMiner';
export const CHOICE_FILE='data-folder-v1.json';
const MAX_CHOICE_BYTES=64*1024;
const MAX_PATH=4096;

const pathsFor=(platform:NodeJS.Platform)=>platform==='win32'?path.win32:path.posix;

export function defaultDataFolder({platform,env,home}:DataFolderEnvironment):string{
  const p=pathsFor(platform);
  if(platform==='win32'){const local=env.LOCALAPPDATA;return p.join(local&&p.isAbsolute(local)?local:p.join(home,'AppData','Local'),DATA_FOLDER_NAME);}
  if(platform==='darwin')return p.join(home,'Library','Application Support',DATA_FOLDER_NAME);
  // XDG: a relative XDG_DATA_HOME is invalid and must be ignored.
  const xdg=env.XDG_DATA_HOME;
  return p.join(xdg&&p.isAbsolute(xdg)?xdg:p.join(home,'.local','share'),DATA_FOLDER_NAME);
}

function validFolder(platform:NodeJS.Platform,value:unknown):value is string{
  return typeof value==='string'&&value.length>0&&value.length<=MAX_PATH&&!value.includes('\0')&&pathsFor(platform).isAbsolute(value);
}

/** The saved choice: null when there is none; a problem when the file exists but can't be used. Never guessed past. */
export function readDataFolderChoice(userData:string,platform:NodeJS.Platform):{path:string}|null|DataFolderProblem{
  const file=path.join(userData,CHOICE_FILE);
  const bad=(problem:string):DataFolderProblem=>({problem,file,remedy:`Remove ${file} to use the default folder, then start VibeMiner again.`});
  let text:string;
  try{
    const bytes=readFileSync(file);
    if(bytes.length>MAX_CHOICE_BYTES)return bad('The data folder setting is too large to be one.');
    text=bytes.toString('utf8');
  }catch(error){
    if((error as NodeJS.ErrnoException).code==='ENOENT')return null;
    return bad(`The data folder setting can't be read (${(error as NodeJS.ErrnoException).code??'error'}).`);
  }
  let value:unknown;
  try{value=JSON.parse(text);}catch{return bad('The data folder setting is not valid JSON.');}
  if(!value||typeof value!=='object'||Array.isArray(value))return bad('The data folder setting is not an object.');
  const record=value as Record<string,unknown>;
  if(typeof record.version==='number'&&record.version>1)return bad('The data folder setting was written by a newer VibeMiner.');
  if(record.version!==1||Object.keys(record).some(k=>k!=='version'&&k!=='path'))return bad('The data folder setting has an unknown shape.');
  if(!validFolder(platform,record.path))return bad('The data folder setting does not name an absolute folder.');
  return{path:pathsFor(platform).normalize(record.path)};
}

/** Saves the choice through a flushed temporary file and a rename, so a crash leaves the old choice or the new one. */
export function writeDataFolderChoice(userData:string,platform:NodeJS.Platform,folder:string):void{
  if(!validFolder(platform,folder))throw new TypeError('A data folder must be an absolute path.');
  mkdirSync(userData,{recursive:true});
  const file=path.join(userData,CHOICE_FILE),temporary=`${file}.tmp`;
  writeFileSync(temporary,JSON.stringify({version:1,path:pathsFor(platform).normalize(folder)})+'\n');
  const fd=openSync(temporary,'r+');try{fsyncSync(fd);}finally{closeSync(fd);}
  renameSync(temporary,file);
}

export function resolveDataFolder(environment:DataFolderEnvironment,choice:{path:string}|null|DataFolderProblem):DataFolder|DataFolderProblem{
  const p=pathsFor(environment.platform);
  const override=environment.env.VIBEMINER_DATA_DIR;
  if(override)return{path:p.resolve(override),source:'override'};
  if(choice&&'problem' in choice)return choice;
  if(choice)return{path:choice.path,source:'chosen'};
  return{path:defaultDataFolder(environment),source:'default'};
}

type MakeFolder=(folder:string,options:{recursive:boolean})=>unknown;
type Probe=(folder:string)=>void;

/** Writes and removes one small file: the only honest test that the engine can write here (mode bits, ACLs, read-only mounts). */
const probeWrite:Probe=folder=>{
  const file=path.join(folder,`.vibeminer-write-check-${randomUUID()}`);
  closeSync(openSync(file,'wx'));
  unlinkSync(file);
};

/**
 * Creates the data folder and checks the engine can write to it. A chosen folder is created only inside a parent that exists,
 * so a disk that isn't mounted is refused instead of quietly filled on the system disk. Any failure is a problem to show, never
 * a fallback.
 */
export function prepareDataFolder(folder:DataFolder,make:MakeFolder=mkdirSync,exists:(p:string)=>boolean=existsSync,probe:Probe=probeWrite):DataFolderProblem|null{
  const remedy='Make the folder available (connect its disk, or fix its permissions), or set another one, then start VibeMiner again.';
  try{
    if(folder.source==='chosen'){
      if(!exists(folder.path)){
        const parent=path.dirname(folder.path);
        if(!exists(parent))return{problem:`The chosen data folder's parent, ${parent}, doesn't exist (a disk that isn't connected?).`,file:folder.path,remedy};
        make(folder.path,{recursive:false});
      }
    }else make(folder.path,{recursive:true});
  }catch(error){
    return{problem:`The data folder can't be created (${(error as NodeJS.ErrnoException).code??'error'}).`,file:folder.path,remedy};
  }
  // An existing folder can still refuse writes (QA on #153: at mode 555 the app started and the engine then exited unnamed).
  try{probe(folder.path);}
  catch(error){return{problem:`The data folder ${folder.path} can't be written to (${(error as NodeJS.ErrnoException).code??'error'}).`,file:folder.path,remedy};}
  return null;
}

type Run=(file:string,args:string[],options:{timeout:number},done:(error:Error|null)=>void)=>void;
const run:Run=(file,args,options,done)=>{execFile(file,args,options,error=>done(error));};

/**
 * macOS only: keeps Time Machine from backing up hundreds of GB it could download again (`tmutil addexclusion` sets a sticky
 * attribute on the folder, so it follows a move). Spotlight still indexes there. Elsewhere this does nothing.
 */
export function excludeFromBackups(folder:string,platform:NodeJS.Platform,runner:Run=run):Promise<'excluded'|'skipped'|'failed'>{
  if(platform!=='darwin')return Promise.resolve('skipped');
  return new Promise(resolve=>runner('/usr/bin/tmutil',['addexclusion',folder],{timeout:10_000},error=>resolve(error?'failed':'excluded')));
}
