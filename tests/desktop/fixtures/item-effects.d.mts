import type {ItemEffectsReadiness,ItemEffectsPage,ItemEffectDetailsResult,ItemEffectNamesResult,ItemEffectsSelectionParams} from '../../../contracts/generated/discovery';
export function effectsFixtures(params?:ItemEffectsSelectionParams,parent?:boolean):{readiness:ItemEffectsReadiness;page:ItemEffectsPage;details:ItemEffectDetailsResult;names:ItemEffectNamesResult};
export function effectsCursor(params:ItemEffectsSelectionParams & {bridgeIndexArtifactId:string;limit?:number},nextKey:string|number):string;
export function effectsScenario(params:ItemEffectsSelectionParams,scenario?:string):ReturnType<typeof effectsFixtures>;
export function effectsNotPrepared(fixtures:ReturnType<typeof effectsFixtures>,phase:'links'|'inputs'|'names'):ItemEffectsReadiness|ItemEffectDetailsResult|ItemEffectNamesResult;
export function narrowEffectInputs<T extends {details:ItemEffectDetailsResult;names:ItemEffectNamesResult}>(fixtures:T,options?:{mixed?:boolean}):T;
