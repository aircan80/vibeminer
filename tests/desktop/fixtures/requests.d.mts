export type FixtureRequest={id:string;method:string;params:any};
export function parseNdjson(text:string):FixtureRequest[];
export function readRequests(directory:string):Promise<FixtureRequest[]>;
