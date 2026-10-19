/** Sole settled main/renderer geometry retirement identities; no transport implementation. */
export type GeometrySceneIdentity=Readonly<{viewId:string;viewGeneration:number;operationToken:string;geometryArtifactId:string}>;
export type GeometryBodyRetired=GeometrySceneIdentity&Readonly<{retired:true}>;
export type GeometrySceneDisposal=Readonly<{viewId:string;operationToken:string|null;scope:'scene'|'view'}>;
