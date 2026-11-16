using System;
using System.IO;
namespace EmpireBay {
// Per-person exclusion only. Never clears offences or changes global recognition.
sealed class PoliceProtection {
 readonly IMemory memory;readonly LuaActions actions;readonly uint image;
 readonly string token="EB_NW_"+Guid.NewGuid().ToString("N");
 uint manager,guid,player;int phase;DateTime nextCheck;
 public bool Desired{get;private set;}public bool Applied{get;private set;}
 public bool Pending{get{return phase!=0;}}
 public bool NeedsCleanup{get{return guid!=0||Pending;}}
 public string Status{get;private set;}="Jamais recherché : désactivé.";
 public PoliceProtection(IMemory m,LuaActions a,uint imageBase){memory=m;actions=a;image=imageBase;}
 public void Request(bool enabled){Desired=enabled;nextCheck=DateTime.MinValue;}
 static uint Find(IMemory m,uint tree,uint key,int nilOffset=0x11){
  uint head=Values.Ptr(m,tree+4),node=Values.Ptr(m,head+4);
  for(int i=0;i<128;i++){if(m.Read(node+(uint)nilOffset,1)[0]!=0)return 0;uint value=Values.U(m,node+12);if(value==key)return node;node=Values.Ptr(m,node+(key<value?0u:8u));}
  throw new InvalidDataException("Structure police non reconnue.");
 }
 uint Manager(){uint registry=Values.Ptr(memory,image+0x18c80e0),entry=Find(memory,registry,4,0x15);if(entry==0)throw new InvalidDataException("Police indisponible.");return Values.Ptr(memory,entry+16);}
 bool Ignored(){if(Values.U(memory,manager+0x4c)>4096)throw new InvalidDataException("Liste police non reconnue.");return Find(memory,manager+0x44,guid)!=0;}
 void Clear(){guid=manager=player=0;phase=0;Applied=false;}
 public void Tick(Snapshot snapshot){
  if(!Desired&&!NeedsCleanup)return;
  if(phase!=0){
   uint state=actions.ExecutionState;if(state==1||state==2)return;
   if(state!=3&&state!=4)return;
   string result=actions.LastStatus;actions.Dispose();int finished=phase;phase=0;
   if(Manager()!=manager){Clear();Desired=false;Status="Jamais recherché arrêté après changement de scène.";return;}
   bool present=Ignored();
   if(finished==1){Applied=present;if(!present){Clear();Desired=false;Status="Activation non confirmée : "+result;return;}if(state==4)Desired=false;Status="Jamais recherché : actif sur Vito.";}
   else if(!present){Clear();Status="Jamais recherché : désactivé, retrait vérifié.";}
   else {Applied=true;Desired=false;Status="Retrait en attente : reprends le jeu sans pause.";nextCheck=DateTime.UtcNow.AddSeconds(2);}
  }
  if(DateTime.UtcNow<nextCheck)return;nextCheck=DateTime.UtcNow.AddMilliseconds(500);
  if(NeedsCleanup){
   if(Manager()!=manager){Clear();Desired=false;Status="Jamais recherché arrêté après changement de scène.";return;}
   if(snapshot.Player!=player||Values.U(memory,snapshot.Player+0x1c)!=guid||snapshot.Health<=0)Desired=false;
   if(Desired){
    // Prévention PERSISTANTE : si le jeu a retiré l'ignore de Vito, on le ré-applique au lieu d'abandonner (l'interrupteur reste ON).
    if(!Ignored()){if(actions.ExecutionState==1||actions.ExecutionState==2)return;actions.Queue(snapshot,"local p=assert(game.game:GetActivePlayer()); local police=assert(ai.police); local g=p:GetGuid(); _G['"+token+"']=g; police:IgnorePerson(g,true)",false);phase=1;Status="Jamais recherché : réapplication en attente (reprends le jeu).";return;}
    Applied=true;Status="Jamais recherché : actif sur Vito.";return;
   }
   // Toggle OFF : retrait propre.
   if(!Ignored()){Clear();Status="Jamais recherché : désactivé, retrait vérifié.";return;}
   if(actions.ExecutionState==1||actions.ExecutionState==2)return;
   actions.Queue(snapshot,"local g=rawget(_G,'"+token+"'); if g then ai.police:IgnorePerson(g,false); _G['"+token+"']=nil end",false);phase=2;Status="Désactivation en attente : reprends le jeu (10 s).";
   return;
  }
  if(!Desired)return;
  if(actions.ExecutionState==1||actions.ExecutionState==2)return;
  if(snapshot.Health<=0){Desired=false;Status="Activation refusée : joueur indisponible.";return;}
  manager=Manager();guid=Values.U(memory,snapshot.Player+0x1c);player=snapshot.Player;
  if(guid==0){Clear();Desired=false;throw new InvalidDataException("GUID joueur absent.");}
  // Même si Vito est déjà ignoré (reste d'une activation précédente), on relance l'action : IgnorePerson est idempotent et surtout l'effacement de recherche doit pouvoir s'exécuter. (Note : si un script de mission ignorait Vito, la désactivation du cheat le retirera aussi.)
  // Prévention seule : IgnorePerson sur Vito (appel validé). L'effacement d'une recherche déjà active a été retiré après analyse (les étoiles vivent dans une structure dynamique, non effaçable proprement) — voir journal. L'activation reste possible même si déjà ignoré/recherché (IgnorePerson idempotent).
  try{actions.Queue(snapshot,"local p=assert(game.game:GetActivePlayer()); local police=assert(ai.police); assert(police.IgnorePerson and p.GetGuid); local g=p:GetGuid(); _G['"+token+"']=g; police:IgnorePerson(g,true)",false);phase=1;Status="Activation en attente : reprends le jeu (10 s).";}
  catch{Clear();Desired=false;throw;}
 }
}
}
