import {useEffect,useLayoutEffect,useRef,useState} from 'react';
import './engine-recovery.css';
import type {DiscoveryBridge} from '../shared/bridge';
import type {EngineStatus} from '../shared/engine-status';
import {App,type Resume} from './App';
const newer=(next:EngineStatus,old:EngineStatus|null)=>!old||next.revision>old.revision;
/**
 * The app over one engine process at a time (D119). When main reports a restartable stop (an invalid reply, or a restart
 * that failed), one modal state covers the whole window: everything below needs the engine, so nothing there can work
 * until Restart engine starts a new process. Once it runs, the app below is mounted afresh, so every result, latch and
 * task of the old process is dropped, and the build and area that were open load again through their normal paths.
 */
export function EngineRecovery({bridge=window.discovery}:{bridge?:DiscoveryBridge}){
  const [status,setStatus]=useState<EngineStatus|null>(null);
  const [mounted,setMounted]=useState<{generation:number;resume:Resume}>({generation:1,resume:null});
  const [problem,setProblem]=useState('');
  const capture=useRef<()=>Resume>(()=>null);
  const take=(next:EngineStatus)=>setStatus(old=>newer(next,old)?next:old);
  useEffect(()=>{
    if(!bridge.onEngineStatus)return;
    let live=true;const off=bridge.onEngineStatus(take);
    void bridge.engineStatus?.().then(reply=>{if(live&&reply.ok)take(reply.value);}).catch(()=>{/* The pushed status still arrives. */});
    return()=>{live=false;off();};
  },[bridge]);
  // A new running process mounts the app afresh; what was open is captured from the old one first.
  useEffect(()=>{if(status?.state==='running'&&status.generation!==mounted.generation){const resume=capture.current();setProblem('');setMounted({generation:status.generation,resume});}},[status]);
  const restart=async()=>{
    if(!bridge.restartEngine||status?.state==='restarting')return;
    setProblem('');
    try{const reply=await bridge.restartEngine();if(reply.ok)take(reply.value);else setProblem(`${reply.error.message} (${reply.error.code})`);}
    catch{setProblem('The restart request did not reach the desktop. Close VibeMiner and start it again.');}
  };
  // The Restart button goes with the old app. A reopened build's area focuses its own heading as it loads; on Discovery,
  // focus goes to the screen's first heading so it never falls to the page.
  useEffect(()=>{if(mounted.generation===1||mounted.resume)return;const frame=requestAnimationFrame(()=>{if(document.activeElement&&document.activeElement!==document.body)return;const heading=[...document.querySelectorAll<HTMLElement>('[data-app-view]:not([hidden]) h1, [data-app-view]:not([hidden]) h2')].find(h=>!h.closest('[hidden],[inert]'));if(heading){if(!heading.hasAttribute('tabindex'))heading.tabIndex=-1;heading.focus({preventScroll:true});}});return()=>cancelAnimationFrame(frame);},[mounted]);
  const shown=!!status&&(status.state==='restarting'||status.state==='stopped'&&status.restartable);
  return <>
    <App key={mounted.generation} bridge={bridge} resume={mounted.resume} captureResume={capture}/>
    {shown&&<EngineStopped status={status!} problem={problem} onRestart={()=>{void restart();}}/>}
  </>;
}
function EngineStopped({status,problem,onRestart}:{status:EngineStatus;problem:string;onRestart:()=>void}){
  const dialog=useRef<HTMLDialogElement>(null),button=useRef<HTMLButtonElement>(null);
  const restarting=status.state==='restarting',failedRestart=status.problem?.code!=='PROTOCOL_ERROR';
  useLayoutEffect(()=>{const node=dialog.current!;node.showModal();button.current?.focus();return()=>{node.close();};},[]);
  const title=restarting?'Restarting the engine':failedRestart?'The engine didn’t restart':'Engine stopped after an invalid reply';
  return <dialog ref={dialog} className="engine-stopped" role="alertdialog" aria-labelledby="engine-stopped-title" aria-describedby="engine-stopped-body" onCancel={event=>event.preventDefault()} onKeyDown={event=>{if(event.key==='Tab'){event.preventDefault();button.current?.focus();}}}>
    <h2 id="engine-stopped-title">{title}</h2>
    <div id="engine-stopped-body">
      {failedRestart
        ?<p>VibeMiner couldn’t get a new engine running. Your data folder wasn’t changed.</p>
        :<p>The local engine sent a reply VibeMiner couldn’t accept, so the reply was refused and the engine was stopped. Nothing from that reply was used, and your data folder wasn’t changed.</p>}
      {status.problem&&<p className="engine-stopped-code"><code>{status.problem.code}</code> {status.problem.message}</p>}
      <p>{restarting?'Starting a new engine…':'Restart the engine to keep working. The build and area you had open load again.'}</p>
    </div>
    {problem&&<p className="engine-stopped-problem" role="alert">{problem}</p>}
    <p role="status" className="visually-hidden">{restarting?'Restarting the engine':''}</p>
    <div className="engine-stopped-actions"><button ref={button} className="primary" aria-disabled={restarting} onClick={()=>{if(!restarting)onRestart();}}>{restarting?'Restarting engine…':'Restart engine'}</button></div>
  </dialog>;
}
