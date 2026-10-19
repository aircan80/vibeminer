import type {OnBeforeRequestListenerDetails,WebContents,Session} from 'electron';
/** Browser-owned metadata is checked in the sole session webRequest listener. */
export function authorizedGeometryFetch(details:Pick<OnBeforeRequestListenerDetails,'webContentsId'|'frame'|'resourceType'|'method'|'initiatorOrigin'>,owner:Pick<WebContents,'id'|'mainFrame'|'isDestroyed'|'session'>|null,appSession:Session,currentDocument:boolean):boolean{
 try{return currentDocument&&!!owner&&!owner.isDestroyed()&&owner.session===appSession&&details.method==='GET'&&details.resourceType==='xhr'&&details.initiatorOrigin==='file://'&&details.webContentsId===owner.id&&!!details.frame&&!details.frame.isDestroyed()&&details.frame===owner.mainFrame;}catch{return false;}
}
/** Protocol.handle receives this independently browser-supplied field, never a page header/referrer. */
export function authorizedGeometryRequest(request:Request):boolean{return (request as Request&{initiatorOrigin?:string}).initiatorOrigin==='file://'&&!request.headers.has('origin');}
