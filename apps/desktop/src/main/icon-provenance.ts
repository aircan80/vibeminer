import type {OnBeforeRequestListenerDetails,WebContents} from 'electron';
// This is the one session webRequest owner for image capabilities. Missing frame data fails closed.
export function authorizedIconImage(details:Pick<OnBeforeRequestListenerDetails,'webContentsId'|'frame'|'resourceType'|'method'>,owner:Pick<WebContents,'id'|'mainFrame'|'isDestroyed'>|null):boolean{
 try{return !!owner&&!owner.isDestroyed()&&details.method==='GET'&&details.resourceType==='image'&&details.webContentsId===owner.id&&!!details.frame&&!details.frame.isDestroyed()&&details.frame===owner.mainFrame;}catch{return false;}
}
