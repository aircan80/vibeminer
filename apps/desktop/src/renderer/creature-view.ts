import type {CreaturesSnapshot,CreaturesPage,CreatureSummary,CreatureDetail} from '../../../../contracts/generated/discovery';
/** What the Creatures view keeps per exact context while another build is shown (at most eight, in useCreaturesLoading). */
export type CreatureView={snapshot:CreaturesSnapshot|null;page:CreaturesPage|null;mode:'name'|'id';draft:string;history:(string|undefined)[];cursor:string|undefined;selected:CreatureSummary|null;detail:CreatureDetail|null;notice:string;listTop:number;pane:'list'|'detail'};
export const emptyCreatureView=():CreatureView=>({snapshot:null,page:null,mode:'name',draft:'',history:[],cursor:undefined,selected:null,detail:null,notice:'',listTop:0,pane:'list'});
