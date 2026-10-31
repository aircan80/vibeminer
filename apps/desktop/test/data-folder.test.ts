// @vitest-environment node
import {chmodSync,mkdtempSync,readdirSync,readFileSync,rmSync,writeFileSync,existsSync} from 'node:fs';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {afterEach,describe,expect,it,vi} from 'vitest';
import {CHOICE_FILE,defaultDataFolder,excludeFromBackups,prepareDataFolder,readDataFolderChoice,resolveDataFolder,writeDataFolderChoice} from '../src/main/data-folder';

// The Windows and macOS rules are checked here as path rules only; those platforms are parked and not run (#30, #31).
describe('default data folder per OS', () => {
  it('uses %LOCALAPPDATA% on Windows, and AppData\\Local when it is missing or relative', () => {
    expect(defaultDataFolder({platform:'win32',env:{LOCALAPPDATA:'C:\\Users\\Ann\\AppData\\Local'},home:'C:\\Users\\Ann'})).toBe('C:\\Users\\Ann\\AppData\\Local\\VibeMiner');
    expect(defaultDataFolder({platform:'win32',env:{},home:'C:\\Users\\Ann'})).toBe('C:\\Users\\Ann\\AppData\\Local\\VibeMiner');
    expect(defaultDataFolder({platform:'win32',env:{LOCALAPPDATA:'relative'},home:'D:\\Home'})).toBe('D:\\Home\\AppData\\Local\\VibeMiner');
  });
  it('uses Application Support on macOS', () => {
    expect(defaultDataFolder({platform:'darwin',env:{XDG_DATA_HOME:'/ignored'},home:'/Users/ann'})).toBe('/Users/ann/Library/Application Support/VibeMiner');
  });
  it('uses $XDG_DATA_HOME or ~/.local/share on Linux, ignoring a relative XDG_DATA_HOME', () => {
    expect(defaultDataFolder({platform:'linux',env:{},home:'/home/ann'})).toBe('/home/ann/.local/share/VibeMiner');
    expect(defaultDataFolder({platform:'linux',env:{XDG_DATA_HOME:'/data/ann'},home:'/home/ann'})).toBe('/data/ann/VibeMiner');
    expect(defaultDataFolder({platform:'linux',env:{XDG_DATA_HOME:'share'},home:'/home/ann'})).toBe('/home/ann/.local/share/VibeMiner');
  });
});

describe('resolution order', () => {
  const linux={platform:'linux' as const,home:'/home/ann'};
  it('lets VIBEMINER_DATA_DIR override a choice, even one that can not be read', () => {
    expect(resolveDataFolder({...linux,env:{VIBEMINER_DATA_DIR:'/tmp/qa/../qa-run'}},{path:'/big/disk'})).toEqual({path:'/tmp/qa-run',source:'override'});
    expect(resolveDataFolder({...linux,env:{VIBEMINER_DATA_DIR:'/tmp/qa'}},{problem:'bad',file:'x',remedy:'r'})).toEqual({path:'/tmp/qa',source:'override'});
  });
  it('uses the chosen folder before the default, and refuses rather than falling back when the choice is unreadable', () => {
    expect(resolveDataFolder({...linux,env:{}},{path:'/big/disk/VibeMiner'})).toEqual({path:'/big/disk/VibeMiner',source:'chosen'});
    expect(resolveDataFolder({...linux,env:{}},null)).toEqual({path:'/home/ann/.local/share/VibeMiner',source:'default'});
    expect(resolveDataFolder({...linux,env:{}},{problem:'bad',file:'x',remedy:'r'})).toEqual({problem:'bad',file:'x',remedy:'r'});
  });
  it('resolves Windows overrides with Windows rules', () => {
    expect(resolveDataFolder({platform:'win32',home:'C:\\Users\\Ann',env:{VIBEMINER_DATA_DIR:'D:\\qa\\.\\run'}},null)).toEqual({path:'D:\\qa\\run',source:'override'});
  });
});

describe('saved choice', () => {
  let folder='';
  afterEach(() => {if(folder)rmSync(folder,{recursive:true,force:true});folder='';});
  const temp=()=>(folder=mkdtempSync(path.join(tmpdir(),'vibeminer-data-folder-')));
  it('round-trips through a flushed temporary file and leaves no temporary behind', () => {
    const userData=temp();
    expect(readDataFolderChoice(userData,'linux')).toBeNull();
    writeDataFolderChoice(userData,'linux','/big/disk//VibeMiner/');
    expect(readDataFolderChoice(userData,'linux')).toEqual({path:'/big/disk/VibeMiner/'});
    expect(JSON.parse(readFileSync(path.join(userData,CHOICE_FILE),'utf8'))).toEqual({version:1,path:'/big/disk/VibeMiner/'});
    expect(existsSync(path.join(userData,`${CHOICE_FILE}.tmp`))).toBe(false);
    expect(()=>writeDataFolderChoice(userData,'linux','relative/folder')).toThrow(TypeError);
  });
  it.each([
    ['not json','{', 'not valid JSON'],
    ['an array','[]','not an object'],
    ['newer','{"version":2,"path":"/x"}','newer VibeMiner'],
    ['an extra field','{"version":1,"path":"/x","more":1}','unknown shape'],
    ['no version','{"path":"/x"}','unknown shape'],
    ['a relative path','{"version":1,"path":"x/y"}','absolute folder'],
    ['a NUL','{"version":1,"path":"/x\\u0000y"}','absolute folder'],
    ['too large',`{"version":1,"path":"/${'x'.repeat(70_000)}"}`,'too large'],
  ])('reports a choice file that is %s as a problem naming the file', (_name,text,message) => {
    const userData=temp();
    writeFileSync(path.join(userData,CHOICE_FILE),text);
    expect(readDataFolderChoice(userData,'linux')).toEqual({problem:expect.stringContaining(message),file:path.join(userData,CHOICE_FILE),remedy:expect.stringContaining('Remove')});
  });
});

describe('creating the folder', () => {
  it('creates the default and the override with their parents', () => {
    const make=vi.fn();
    expect(prepareDataFolder({path:'/home/ann/.local/share/VibeMiner',source:'default'},make,()=>false,()=>{})).toBeNull();
    expect(make).toHaveBeenCalledWith('/home/ann/.local/share/VibeMiner',{recursive:true});
  });
  it('creates a chosen folder only inside a parent that exists, so an unmounted disk is refused', () => {
    const make=vi.fn();
    expect(prepareDataFolder({path:'/mnt/big/VibeMiner',source:'chosen'},make,p=>p==='/mnt/big',()=>{})).toBeNull();
    expect(make).toHaveBeenCalledWith('/mnt/big/VibeMiner',{recursive:false});
    expect(prepareDataFolder({path:'/mnt/big/VibeMiner',source:'chosen'},make,()=>true,()=>{})).toBeNull();
    expect(make).toHaveBeenCalledTimes(1);
    expect(prepareDataFolder({path:'/mnt/gone/VibeMiner',source:'chosen'},make,()=>false,()=>{})).toMatchObject({problem:expect.stringContaining('/mnt/gone'),file:'/mnt/gone/VibeMiner'});
  });
  it('turns a failure to create the folder into a problem to show, not a throw', () => {
    const make=()=>{throw Object.assign(new Error('denied'),{code:'EACCES'});};
    expect(prepareDataFolder({path:'/root/VibeMiner',source:'default'},make)).toEqual({problem:expect.stringContaining('EACCES'),file:'/root/VibeMiner',remedy:expect.any(String)});
  });
});

describe('a folder that refuses writes', () => {
  let folder='';
  afterEach(() => {if(folder){chmodSync(folder,0o755);rmSync(folder,{recursive:true,force:true});}folder='';});
  it('is named when the write probe fails', () => {
    const probe=()=>{throw Object.assign(new Error('read-only'),{code:'EROFS'});};
    expect(prepareDataFolder({path:'/media/ro/VibeMiner',source:'default'},()=>undefined,()=>true,probe)).toEqual({problem:"The data folder /media/ro/VibeMiner can't be written to (EROFS).",file:'/media/ro/VibeMiner',remedy:expect.any(String)});
  });
  it('refuses a real folder at mode 555 and leaves a writable one as it was', () => {
    folder=mkdtempSync(path.join(tmpdir(),'vibeminer-data-folder-ro-'));
    expect(prepareDataFolder({path:folder,source:'chosen'})).toBeNull();
    expect(readdirSync(folder)).toEqual([]);  // the probe file is gone
    chmodSync(folder,0o555);
    // root ignores mode bits, so the real refusal can only be seen as a normal user.
    if(process.getuid?.()===0)return;
    expect(prepareDataFolder({path:folder,source:'chosen'})).toMatchObject({problem:expect.stringContaining('EACCES'),file:folder});
    expect(prepareDataFolder({path:folder,source:'override'})).toMatchObject({problem:expect.stringContaining("can't be written to")});
  });
});

describe('Time Machine exclusion', () => {
  it('runs tmutil addexclusion on macOS only', async () => {
    const runner=vi.fn((_f:string,_a:string[],_o:{timeout:number},done:(e:Error|null)=>void)=>done(null));
    expect(await excludeFromBackups('/Users/ann/Library/Application Support/VibeMiner','darwin',runner)).toBe('excluded');
    expect(runner).toHaveBeenCalledWith('/usr/bin/tmutil',['addexclusion','/Users/ann/Library/Application Support/VibeMiner'],{timeout:10_000},expect.any(Function));
    for(const platform of ['linux','win32'] as const)expect(await excludeFromBackups('/x',platform,runner)).toBe('skipped');
    expect(runner).toHaveBeenCalledTimes(1);
  });
  it('reports a failed exclusion without throwing', async () => {
    expect(await excludeFromBackups('/x','darwin',(_f,_a,_o,done)=>done(new Error('no tmutil')))).toBe('failed');
  });
});
