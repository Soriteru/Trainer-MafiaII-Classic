using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace EmpireBay {
sealed class WeaponEntry {
 // Exclues du give : MG42 fixe (ID 14, non portable) et Stielhandgranate 24 (ID 16, jetée à terre sans effet — non utilisable par Vito en jeu libre, confirmé 01/11).
 public int Id;public string Name;public bool FromGame; public bool GiveTested { get { return Id!=14&&Id!=16; } } public bool GiveValidated { get { return GiveTested && FromGame; } }
 public override string ToString(){return Name+" · ID "+Id+(GiveTested?(FromGame?" · AJOUT DISPONIBLE":" · AJOUT DISPONIBLE après lecture du catalogue"):" · NON DONNABLE (non utilisable en jeu)");}
}
static class ActionScripts {
 public static readonly Dictionary<int,string> KnownWeapons=new Dictionary<int,string>{
  {2,"Revolver .38"},{3,"Mauser C96"},{4,"Colt M1911A1"},{5,"Colt M1911 Special"},{6,"Revolver .357"},
  {7,"Grenade Sicily"},{8,"Remington 870"},{9,"M3 Grease Gun"},{10,"MP40"},{11,"Thompson 1928"},{12,"M1A1 Thompson"},{13,"Beretta 38A"},{14,"MG42"},{15,"M1 Garand"},{16,"Stielhandgranate 24"},{17,"Kar98k"},{18,"Mauser 98k sniper"},{19,"Bazooka"},{20,"Grenade MK2"},{21,"Cocktail Molotov"},{126,".357 Magnum DLC"}
 };
 // Projectiles (grenades, cocktails, bazooka) : stock bien plus limité que les armes à feu → plafond prudent pour éviter tout débordement.
 public const int ProjectileMax=10;
 public const int FirearmMax=300;
 public static bool IsProjectile(int id){return id==7||id==16||id==19||id==20||id==21;}
 public static int CapAmmo(int id,int ammo){int max=IsProjectile(id)?ProjectileMax:FirearmMax;if(ammo>max)ammo=max;if(ammo<1)ammo=1;return ammo;}
 public static string ClearWanted(){throw new InvalidOperationException("Effacement police désactivé après un crash signalé. Aucune commande exécutée.");}
 static string Vehicle(){return "local p=assert(game.game:GetActivePlayer(),'player unavailable'); local v=assert(p:GetOwner(),'no vehicle'); ";}
 public static string Repair(){throw new InvalidOperationException("Réparation complète suspendue après crash lors d’une collision.");}
 public static string RepairMotorProbe(){return Vehicle()+"assert(v.SetMotorDamage and v.GetMotorDamage,'motor API unavailable'); v:SetMotorDamage(0); local d=v:GetMotorDamage(); assert(type(d)=='number' and d>=0 and d<0.001,'motor repair not confirmed')";}
 public static string Refuel(){throw new InvalidOperationException("Plein suspendu après un crash signalé lors d’une collision.");}
 public static string GiveWeapon(int id,int ammo){if(id<2||id>=1000||ammo<1||ammo>999)throw new ArgumentOutOfRangeException();return "local p=assert(game.game:GetActivePlayer(),'player unavailable'); p:InventoryAddWeapon("+id+","+ammo+"); assert(p:InventoryHaveItem("+id+"),'weapon not transportable or unavailable')";}
}
// Candidate only. Commands are posted to the player's input callback, not a remote thread.
sealed class LuaActions:IDisposable {
 readonly IPatchMemory memory;readonly uint image,root;
 uint block;bool installed,removeAfterCompletion;uint spawnBlock;int spawnCount;DateTime queuedAt;byte[] patch;
 const uint HookRva=0x3bd42;public string LastStatus="Nouvelles actions non validées en jeu.";
 public uint LastCompletionState{get;private set;}
 public LuaActions(IPatchMemory m,uint imageBase,uint singleton){memory=m;image=imageBase;root=singleton;}
 public List<WeaponEntry> Catalogue(){
  uint tables=Values.Ptr(memory,image+0x16b5a60),wrapper=Values.Ptr(memory,tables+0x20),table=Values.Ptr(memory,wrapper+4);
  int count=(int)Values.U(memory,table+8),stride=(int)Values.U(memory,table+0x14);uint rows=Values.Ptr(memory,table+0x1c);
  if(count<1||count>512||stride<0x28||stride>4096)throw new InvalidDataException("Table des armes non reconnue.");
  var result=new List<WeaponEntry>();var ids=new HashSet<int>();
  for(int row=0;row<count;row++){int id=(int)Values.U(memory,rows+(uint)(row*stride));if(!ActionScripts.KnownWeapons.ContainsKey(id)||!ids.Add(id))continue;string name;ActionScripts.KnownWeapons.TryGetValue(id,out name);if(name==null){byte[] label=memory.Read(rows+(uint)(row*stride)+4,32);name=Encoding.Latin1.GetString(label).Split('\0')[0].Trim();if(name.Length==0)name=null;}result.Add(new WeaponEntry{Id=id,Name=name??"Arme / objet de mission à identifier",FromGame=true});}
  if(result.Count==0)throw new InvalidDataException("Aucune arme identifiable dans la table.");return result.OrderByDescending(w=>w.GiveTested).ThenBy(w=>w.Id).ToList();
 }
 uint LuaState(){uint manager=Values.Ptr(memory,image+0x16c0c78),machine=Values.Ptr(memory,manager+4),pool=Values.Ptr(memory,machine+4),entry=Values.Ptr(memory,pool);return Values.Ptr(memory,entry+0x50);}
 void Install(){
  if(installed)return;
  // Auto-réparation : octets d'origine OK, ou reste d'un de nos hooks (saut 0xE9, restauration précédente incomplète) → on réinstalle par-dessus. On refuse seulement un contenu vraiment inconnu (autre outil).
  byte[] original=Original(image),curcb=memory.Read(image+HookRva,5);if(!curcb.SequenceEqual(original)&&curcb[0]!=0xe9)throw new InvalidDataException("Callback du joueur différent. Action refusée.");
  foreach(uint rva in new uint[]{0x1d5bd0,0x1d3f80})if(!memory.Read(image+rva,3).SequenceEqual(new byte[]{0x55,0x8b,0xec}))throw new InvalidDataException("Moteur Lua différent : action refusée.");
  block=memory.Allocate(8192);memory.Write(block,new byte[40]);memory.Write(block+0x300,Encoding.ASCII.GetBytes("EmpireBay-v04-test\0"));
  memory.Write(block+0x500,DispatchCode(block,image,root));patch=new byte[5];patch[0]=0xe9;Array.Copy(BitConverter.GetBytes(unchecked(block+0x500-(image+HookRva+5))),0,patch,1,4);
  memory.Patch(image+HookRva,patch);installed=true;
 }
 public static byte[] Original(uint image){var b=new byte[5];b[0]=0xe8;Array.Copy(BitConverter.GetBytes(unchecked((image+0x2abe0)-(image+HookRva+5))),0,b,1,4);return b;}
 public void Queue(Snapshot snapshot,string script,bool vehicle,bool removeCallback=false){
  if(Encoding.UTF8.GetByteCount(script)>2047)throw new ArgumentException("Commande trop longue.");
  Poll();Install();uint state=Values.U(memory,block);if(state==1||state==2)throw new InvalidOperationException("Une action est déjà en attente.");
  if(Values.Ptr(memory,Values.Ptr(memory,root)+0x118)!=snapshot.Player)throw new InvalidDataException("Le joueur a changé.");
  uint car=vehicle?Values.Ptr(memory,snapshot.Player+0x64):0;
  byte[] source=Encoding.UTF8.GetBytes(script);memory.Write(block,new byte[40]);memory.Write(block+0x1000,source);memory.Write(block+0x1000+(uint)source.Length,new byte[]{0});
  memory.Write(block+8,BitConverter.GetBytes(snapshot.Player));memory.Write(block+12,BitConverter.GetBytes(car));memory.Write(block+20,BitConverter.GetBytes(LuaState()));memory.Write(block+24,BitConverter.GetBytes(source.Length));memory.Write(block+4,BitConverter.GetBytes(1u));
  LastCompletionState=0;removeAfterCompletion=removeCallback;queuedAt=DateTime.UtcNow;memory.Write(block,BitConverter.GetBytes(1u));LastStatus="Action en attente du prochain tick du jeu (10 s maximum).";
 }
 public List<VehicleEntry> Vehicles(){return VehicleSpawner.Catalogue(memory,image);}
 public void Spawn(Snapshot snapshot,VehicleEntry entry){
  if(!VehicleSpawner.Enabled)throw new InvalidOperationException("Spawner désactivé après crash au crochetage de la Smith V8 ; validation à reprendre.");
  if(snapshot.Health<=0)throw new InvalidOperationException("Le joueur doit être vivant.");
  if(snapshot.Car!=0)throw new InvalidOperationException("Sors du véhicule avant de faire apparaître une voiture.");
  if(spawnCount>=3)throw new InvalidOperationException("Limite de trois créations pour cette connexion. Reprends une partie avant un nouvel essai.");
  if(entry==null)throw new InvalidOperationException("Lis les modèles chargés puis sélectionne une voiture.");
  Poll();Install();if(Values.U(memory,block)==1||Values.U(memory,block)==2)throw new InvalidOperationException("Une action est déjà en attente.");
  if(!VehicleSpawner.Allowed(entry.Name))throw new InvalidOperationException("Modèle non validé : création refusée.");
  VehicleSpawner.Validate(memory,image,entry);
  if(spawnBlock==0)spawnBlock=memory.Allocate(8192);
  memory.Write(spawnBlock,new byte[8192]);VehicleSpawner.Prepare(memory,spawnBlock,image,snapshot.Player,entry);
  memory.Write(block,new byte[40]);memory.Write(block+40,new byte[256]);memory.Write(block+8,BitConverter.GetBytes(snapshot.Player));memory.Write(block+28,BitConverter.GetBytes(spawnBlock+0x500));memory.Write(block+4,BitConverter.GetBytes(1u));
  LastCompletionState=0;removeAfterCompletion=false;queuedAt=DateTime.UtcNow;memory.Write(block,BitConverter.GetBytes(1u));spawnCount++;LastStatus="Création en attente du prochain tick du jeu (10 s maximum).";
 }
 public uint SpawnedCar{get{return spawnBlock==0?0:Values.U(memory,spawnBlock+24);}}
 public uint SpawnStage{get{return spawnBlock==0?0:Values.U(memory,spawnBlock+4);}}
 public uint ExecutionState{get{return installed?Values.U(memory,block):0;}}
 public string ErrorDebug(){return Encoding.UTF8.GetString(memory.Read(block+40,256)).Split('\0')[0];} public string Poll(){
  if(!installed)return LastStatus;uint state=Values.U(memory,block);
  if(state==3||state==4)LastCompletionState=state;
  if(state==1&&(DateTime.UtcNow-queuedAt).TotalSeconds>10){memory.Write(block+4,BitConverter.GetBytes(0u));LastStatus="Demande expirée pendant la pause : relance l'action après reprise du jeu.";}
  if(state==3)LastStatus=Values.U(memory,block+28)!=0?"Véhicule créé devant Vito ; vérifie son affichage et sa conduite.":"Action exécutée ; résultat visuel à vérifier en jeu.";
  if(state==4)LastStatus=Values.U(memory,block+28)!=0?"Création refusée à l’étape "+SpawnStage+" (code "+Values.U(memory,block+16)+"). Relis les modèles après chargement.":"Action refusée / erreur Lua (code "+Values.U(memory,block+16)+"). "+ErrorDebug();
  if((state==3||state==4)&&(removeAfterCompletion||Values.U(memory,block+28)!=0))Dispose();
  else if(state==1&&removeAfterCompletion&&(DateTime.UtcNow-queuedAt).TotalSeconds>10){LastCompletionState=5;Dispose();}
  return LastStatus;
 }
 public void CancelPending(){if(installed&&Values.U(memory,block)==1){memory.Write(block+4,BitConverter.GetBytes(0u));LastStatus="Demande annulée avant exécution.";}}
 public static byte[] DispatchCode(uint block,uint image,uint root){
  var a=new X86();uint code=block+0x500;
  Call(a,image+0x2abe0,code);a.Emit(0x9c,0x60);
  a.Emit(0x81,0xec,0x10,0x02,0,0,0x8b,0xfc,0x83,0xc7,0x0f,0x83,0xe7,0xf0,0x0f,0xae,0x07); // Aligned FXSAVE: preserve SSE/x87 state.
  a.Emit(0x83,0x3d);a.Number(block);a.Emit(1);a.Jump(0x85,"end");
  a.Emit(0x83,0x3d);a.Number(block+4);a.Emit(0);a.Jump(0x84,"cancel");
  a.Emit(0xb8);a.Number(1);a.Emit(0xba);a.Number(2);a.Emit(0xf0,0x0f,0xb1,0x15);a.Number(block);a.Jump(0x85,"end"); // Atomically claim once.
  a.Emit(0xa1);a.Number(root);a.Emit(0x85,0xc0);a.Jump(0x84,"cancel");a.Emit(0x8b,0x80,0x18,0x01,0,0,0x3b,0x05);a.Number(block+8);a.Jump(0x85,"cancel");
  a.Emit(0x8b,0x15);a.Number(block+12);a.Emit(0x85,0xd2);a.Jump(0x84,"lua");a.Emit(0x3b,0x50,0x64);a.Jump(0x85,"cancel");
  a.Label("lua");a.Emit(0xa1);a.Number(block+28);a.Emit(0x85,0xc0);a.Jump(0x84,"luaOnly");a.Emit(0xff,0xd0,0x85,0xc0);a.Jump(0x85,"nativeError");Store(a,block,3);a.JumpAlways("end");a.Label("nativeError");a.Emit(0xa3);a.Number(block+16);Store(a,block,4);a.JumpAlways("end");a.Label("luaOnly");a.Emit(0xa1);a.Number(image+0x16c0c78);a.Emit(0x85,0xc0);a.Jump(0x84,"cancel");
  foreach(byte offset in new byte[]{4,4,0,0x50}){a.Emit(0x8b,0x40,offset,0x85,0xc0);a.Jump(0x84,"cancel");}
  a.Emit(0x3b,0x05);a.Number(block+20);a.Jump(0x85,"cancel");a.Emit(0x8b,0xf0,0x8b,0x5e,0x08,0x2b,0x5e,0x20); // Save top as offset, safe if Lua reallocates its stack.
  a.Emit(0x6a,0,0x68);a.Number(block+0x300);a.Emit(0xff,0x35);a.Number(block+24);a.Emit(0x68);a.Number(block+0x1000);a.Emit(0x56);Call(a,image+0x1d5bd0,code);a.Emit(0x83,0xc4,0x14,0x85,0xc0);a.Jump(0x85,"luaError");
  a.Emit(0x6a,0,0x6a,0,0x6a,0,0x56);Call(a,image+0x1d3f80,code);a.Emit(0x83,0xc4,0x10,0x85,0xc0);a.Jump(0x85,"luaError");
  Store(a,block,3);a.Jump(0x84,"restore"); // TEST eax,eax above was zero; STORE preserves flags.
  a.Label("luaError");a.Emit(0xa3);a.Number(block+16);Store(a,block,4);
  a.Emit(0x8b,0x56,0x08,0x83,0x7a,0xfc,4);a.Jump(0x85,"restore");
  a.Emit(0x8b,0x52,0xf8,0x83,0xc2,0x10,0x31,0xc9);
  a.Label("copyError");a.Emit(0x8a,0x02,0x88,0x81);a.Number(block+40);a.Emit(0x84,0xc0);a.Jump(0x84,"restore");a.Emit(0x42,0x41,0x81,0xf9,0xff,0,0,0);a.Jump(0x82,"copyError");
  a.Label("restore");a.Emit(0x8b,0x56,0x20,0x03,0xd3,0x89,0x56,0x08);a.JumpAlways("end");
  a.Label("cancel");Store(a,block+16,0xfffffffe);Store(a,block,4);
  a.Label("end");a.Emit(0x0f,0xae,0x0f,0x81,0xc4,0x10,0x02,0,0,0x61,0x9d);a.Tail(image+HookRva+5,code);return a.Finish();
 }
 static void Call(X86 a,uint dest,uint code){a.Emit(0xe8);a.Number(unchecked(dest-(code+(uint)a.Code.Count+4)));}
 static void Store(X86 a,uint address,uint value){a.Emit(0xc7,0x05);a.Number(address);a.Number(value);}
 public void Dispose(){if(!installed)return;memory.Write(block+4,BitConverter.GetBytes(0u));if(Values.U(memory,block)==2){LastStatus="Action déjà démarrée : callback conservé mais désactivé jusqu'à la fermeture du jeu.";return;}if(memory.Read(image+HookRva,5).SequenceEqual(patch))memory.Patch(image+HookRva,Original(image));installed=false;}
}
static class ExperimentalTests {
 static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
 public static void Run(){
  bool refuelBlocked=false;try{ActionScripts.Refuel();}catch(InvalidOperationException){refuelBlocked=true;}Check(refuelBlocked,"Refuel command blocked pending collision diagnosis");
  bool repairBlocked=false;try{ActionScripts.Repair();}catch(InvalidOperationException){repairBlocked=true;}Check(repairBlocked,"Full vehicle rebuild blocked after collision crash");
  Check(!ActionScripts.RepairMotorProbe().Contains("RepairAndClear")&&!ActionScripts.RepairMotorProbe().Contains("v:Repair()"),"Motor-only probe must not rebuild body");
  bool refused=false;try{ActionScripts.GiveWeapon(1,120);}catch(ArgumentOutOfRangeException){refused=true;}Check(refused,"Refuse fists/invalid weapon");
  refused=false;try{ActionScripts.GiveWeapon(15,1000);}catch(ArgumentOutOfRangeException){refused=true;}Check(refused,"Ammo bound");
  Check(ActionScripts.GiveWeapon(15,120).Contains("InventoryHaveItem(15)"),"Check weapon ownership after give");
  refused=false;try{ActionScripts.ClearWanted();}catch(InvalidOperationException){refused=true;}Check(refused,"Police action blocked before any game command");
  var memory=new FakePatchMemory();uint image=0x400000,root=0x10000,player=0x30000;
  Action<uint,uint> u=(a,v)=>memory.Write(a,BitConverter.GetBytes(v));
  memory.Write(image+0x3bd42,LuaActions.Original(image));memory.Write(image+0x1d5bd0,new byte[]{0x55,0x8b,0xec});memory.Write(image+0x1d3f80,new byte[]{0x55,0x8b,0xec});
  u(root,0x20000);u(0x20118,player);u(image+0x16c0c78,0xa0000);u(0xa0004,0xa0100);u(0xa0104,0xa0200);u(0xa0200,0xa0300);u(0xa0350,0xa0400);
  memory.Write(0xb0200,new byte[0xc0]);u(image+0x16b5a60,0xb0000);u(0xb0020,0xb0100);u(0xb0104,0xb0150);u(0xb0158,3);u(0xb0164,0x40);u(0xb016c,0xb0200);u(0xb0200,15);u(0xb0240,2);u(0xb0280,76);
  using(var actions=new LuaActions(memory,image,root)){
   var catalogue=actions.Catalogue();Check(catalogue.Count==2&&catalogue.All(w=>w.Id!=76&&w.FromGame),"Exclude props and unidentified entries from weapon catalogue");
   actions.Queue(new Snapshot{Player=player},"return",false);Check(Values.U(memory,0x100000)==1,"Queue exactly once");
   refused=false;try{actions.Queue(new Snapshot{Player=player},"return",false);}catch(InvalidOperationException){refused=true;}Check(refused,"Reject overlapping command");
   actions.CancelPending();Check(Values.U(memory,0x100004)==0,"Cancellation flag before execution");
  }
  Check(memory.Read(image+0x3bd42,5).SequenceEqual(LuaActions.Original(image)),"Restore input callback");
  using(var motor=new LuaActions(memory,image,root)){
   motor.Queue(new Snapshot{Player=player},"return",false,true);u(0x100000,3);motor.Poll();
   Check(memory.Read(image+0x3bd42,5).SequenceEqual(LuaActions.Original(image)),"Motor completion removes temporary callback");
   motor.Queue(new Snapshot{Player=player},"return",false,true);u(0x100000,3);
   motor.Queue(new Snapshot{Player=player},"return",false,true);
   Check(Values.U(memory,0x100000)==1&&!memory.Read(image+0x3bd42,5).SequenceEqual(LuaActions.Original(image)),"Immediate repeat reinstalls completed one-shot callback");motor.CancelPending();
  }
  using(var completed=new LuaActions(memory,image,root)){
   completed.Queue(new Snapshot{Player=player},"return",false);u(0x10001c,0x120500);u(0x100000,3);completed.Poll();
   Check(memory.Read(image+0x3bd42,5).SequenceEqual(LuaActions.Original(image)),"Native completion removes input hook before interaction");
   completed.Queue(new Snapshot{Player=player},"return",false);Check(Values.U(memory,0x10001c)==0,"Next Lua action has no stale native callback");completed.CancelPending();
  }

  Check(ForceLoad.BaseName("cars/hot_rod_2")=="hot_rod_2","Force base name strips folder");
  Check(ForceLoad.BaseName("/sds/cars/berkley_810.sds")=="berkley_810","Force base name strips path and extension");
  Check(ForceLoad.BaseName("bolt_z")=="bolt","Force base name strips scene suffix");
  using(var la=new LuaActions(memory,image,root)){var fl=new ForceLoad(la);
   bool badName=false;try{fl.Request("nom invalide!");}catch(InvalidOperationException){badName=true;}Check(badName,"Force rejects invalid SDS name");
   Check(!fl.Active,"Force idle after rejected request");
   fl.Request("cars/hot_rod_2");Check(fl.Active,"Force active after valid request");
   bool dup=false;try{fl.Request("cars/other");}catch(InvalidOperationException){dup=true;}Check(dup,"Force rejects a second concurrent request");
   fl.Cancel();Check(!fl.Active,"Force cancel returns to idle");
  }
  memory.Write(image+0x5a46e0,new byte[]{0x8b,0x41,0x08,0x8b,0x54,0x24,0x04});memory.Write(image+0x20e60,new byte[]{0x53,0x56,0x57,0x8b,0xf1});memory.Write(image+0x208b0,new byte[]{0x83,0xec,0x2c,0x53,0x56});
  using(var hooks=new PlayerHooks(memory,root,image)){hooks.Set(true,true,player,0x40000,true);Check(memory.Read(0x100002,1)[0]==1,"Combined precision flag");hooks.Set(true,true,player,0x40000,false);Check(memory.Read(0x100002,1)[0]==0,"Precision can be disabled independently of recoil");}
  Check(memory.Read(image+0x208b0,5).SequenceEqual(new byte[]{0x83,0xec,0x2c,0x53,0x56}),"Restore precision routine");
  File.WriteAllText("experimental-tests.txt","PASS: script bounds, give verification, weapon-only catalogue excluding scene props, queue exclusion, cancellation and callback restoration. No game process accessed.");
 }
}
}







