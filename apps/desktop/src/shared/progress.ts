import type { EngineProgress } from '../../../../contracts/generated/discovery';
/** Structural schema validation remains at the process boundary; these are relational invariants. */
export function validProgressStep(step: EngineProgress['step']): boolean {
  if (step === undefined || step === null) return true;
  return Number.isSafeInteger(step.index) && step.index >= 1 && (step.total === null ? step.index === 1 : Number.isSafeInteger(step.total) && step.total >= step.index) && typeof step.description === 'string' && step.description.trim().length > 0 && step.description.length <= 400;
}

/** Effects counters are safe JS integers and always identify only a consulted source role. */
export function validItemEffectsProgress(progress:EngineProgress):boolean {
  const text=(v:unknown,max:number)=>typeof v==='string'&&!!v.trim()&&v.length<=max&&!/[\x00-\x1f\x7f-\x9f]/.test(v);
  const count=(v:number|null)=>v===null||Number.isSafeInteger(v)&&v>=0;
  return Number.isSafeInteger(progress.sequence)&&progress.sequence>=1&&text(progress.stage,128)&&text(progress.unit,32)&&count(progress.completed)&&count(progress.total)&&(progress.completed===null||progress.total===null||progress.completed<=progress.total)&&Object.hasOwn(progress,'tableId')&&(progress.tableId===null||['00cb674f','4002a5b1','46c66698'].includes(progress.tableId??''))&&validProgressStep(progress.step);
}
