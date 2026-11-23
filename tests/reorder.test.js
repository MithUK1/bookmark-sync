// Checks the extension's reorder step against a fake bookmarks API.
// Run: node tests/reorder.test.js [chrome|firefox]   (chrome mimics its move position rule, firefox takes the final position)
const fs=require('fs'),vm=require('vm');
const quirk=process.argv[2]==='chrome';
const firefox=process.argv[2]==='firefox';
let n=10;const nodes={};
function mk(o){const id=String(++n);const x={id,children:undefined,...o};nodes[id]=x;return x}
const root={id:'0',title:'',children:[]};nodes['0']=root;
for(const [id,t] of [['1','Bookmarks bar'],['2','Other bookmarks']]){const r={id,title:t,children:[],parentId:'0'};nodes[id]=r;root.children.push(r)}
function add(parent,o){const x=mk({parentId:parent.id,...o});if(o.url===undefined)x.children=[];parent.children.push(x);return x}
const bar=nodes['1'];
const f=add(bar,{title:'F'});
for(const u of ['a','b','c','d'])add(f,{title:u,url:'https://'+u+'.test'});
add(bar,{title:'x',url:'https://x.test'});
const g=add(bar,{title:'G'});
for(let i=0;i<40;i++)add(g,{title:'g'+i,url:'https://g'+i+'.test'});
function idx(){for(const p of Object.values(nodes))if(p.children)p.children.forEach((c,i)=>c.index=i)}
const api={bookmarks:{
 async getTree(){idx();return [JSON.parse(JSON.stringify(root))]},
 async getChildren(id){idx();return JSON.parse(JSON.stringify(nodes[id].children))},
 async move(id,{parentId,index}){const x=nodes[id];const old=nodes[x.parentId];const oi=old.children.indexOf(x);old.children.splice(oi,1);const p=nodes[parentId];let i=index;if(quirk&&old===p&&index>oi)i=index-1;p.children.splice(i,0,x);x.parentId=parentId;return x},
 async create(o){return add(nodes[o.parentId],o)},
 async remove(id){const x=nodes[id];const p=nodes[x.parentId];p.children.splice(p.children.indexOf(x),1)},
 onCreated:{addListener(){}},onRemoved:{addListener(){}},onChanged:{addListener(){}},onMoved:{addListener(){}},onImportEnded:{addListener(){}}},
 alarms:{create(){},get:async()=>null,onAlarm:{addListener(){}}},
 storage:{local:{set:async()=>{},get:async()=>({})}},
 windows:{onRemoved:{addListener(){}},create:async()=>({id:1})},action:{},runtime:{getManifest(){return{version:"1.0.2"}},onStartup:{addListener(){}},onInstalled:{addListener(){}},onMessage:{addListener(){}},sendNativeMessage:async()=>{},connectNative(){return{onMessage:{addListener(){}},onDisconnect:{addListener(){}},postMessage(){}}}}};
api.runtime.getBrowserInfo=async()=>({});
const ctx={chrome:api,...(firefox?{browser:api}:{}),navigator:{userAgent:'Chrome'},URL,atob,TextDecoder,console,setTimeout,clearTimeout};
ctx.globalThis=ctx;vm.createContext(ctx);
vm.runInContext(fs.readFileSync(require('path').join(__dirname,'..','extension','src','background.js'),'utf8')+';this.apply=apply;this.snapshot=snapshot',ctx);
const shuffled=Array.from({length:40},(_,i)=>'b|//g'+i+'.test');
let seed=7;const rnd=()=>(seed=(seed*16807)%2147483647)/2147483647;
for(let i=shuffled.length-1;i>0;i--){const j=Math.floor(rnd()*(i+1));[shuffled[i],shuffled[j]]=[shuffled[j],shuffled[i]];}
(async()=>{
 const target={bookmarks:[],folders:[['toolbar','F']],order:[
  {path:['toolbar'],items:['f|G','b|//x.test','f|F']},
  {path:['toolbar','F'],items:['b|//d.test','b|//b.test','b|//a.test','b|//c.test']},{path:['toolbar','G'],items:shuffled},{path:['other'],items:[]}]};
 target.bookmarks=(await ctx.snapshot()).bookmarks.map(b=>({...b}));
 const st=await ctx.apply(target);
 const out=(await ctx.snapshot()).order;
 console.log(quirk?'chrome':'firefox',JSON.stringify(st));
 const ok=JSON.stringify(out.find(o=>o.path.join()==='toolbar,G').items)===JSON.stringify(shuffled)&&JSON.stringify(out.find(o=>o.path.join()==='toolbar,F').items)===JSON.stringify(target.order[1].items)&&JSON.stringify(out[0].items)===JSON.stringify(target.order[0].items);
 console.log(ok?'PASS':'FAIL '+JSON.stringify(out));process.exit(ok?0:1)})();
