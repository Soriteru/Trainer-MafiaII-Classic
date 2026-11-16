using System;
namespace EmpireBay {
// The game scheduler repairs the same occupied, stationary car after submission.
sealed class BodyRepair {
 readonly IMemory memory;readonly LuaActions actions;readonly Func<DateTime> now;
 readonly string token="EB_BODY_"+Guid.NewGuid().ToString("N");
 int phase,retries;DateTime nextCheck;
 public bool Busy{get{return phase!=0;}}
 public string Status{get;private set;}="Véhicule : monte au volant, à l'arrêt, puis clique Réparer.";
 public BodyRepair(IMemory m,LuaActions a,Func<DateTime> clock=null){memory=m;actions=a;now=clock??(()=>DateTime.UtcNow);}
 void Idle(string status){phase=0;Status=status;}
 public void Repair(Snapshot s){
  if(Busy||actions.ExecutionState==1||actions.ExecutionState==2)throw new InvalidOperationException("Attends la fin de l’action en cours.");
  if(s.Car==0||s.Health<=0||Values.U(memory,s.Player+0x64)!=s.Car)throw new InvalidOperationException("Reste seul au volant de la voiture garée.");
  if(Values.U(memory,s.Car+0x1c)==0)throw new InvalidOperationException("Identité du véhicule absente.");
  actions.Queue(s,"local p=assert(game.game:GetActivePlayer()); local v=assert(p:GetOwner()); if v.SetMotorDamage then v:SetMotorDamage(0) end; assert(math.abs(v:GetSpeedFloat())<0.3,'voiture en mouvement'); assert(DelayBuffer and DelayBuffer.Insert,'scheduler absent'); local t={guid=v:GetGuid(),status='queued'}; _G['"+token+"']=t; DelayBuffer:Insert(function() local ok,err=pcall(function() local p=assert(game.game:GetActivePlayer()); local v=assert(p:GetOwner(),'joueur sorti'); local target=assert(game.entitywrapper:GetEntityByGUID(t.guid),'voiture absente'); assert(v==target,'voiture changee'); assert(math.abs(v:GetSpeedFloat())<0.3,'voiture en mouvement'); v:RepairAndClear() end); t.status=ok and 'completed' or ('error '..tostring(err)) end,nil,500,1,false)",true,true);
  phase=1;retries=0;Status="Véhicule : reprends le jeu et reste immobile au volant jusqu’à confirmation.";
 }
 public void Tick(Snapshot s){
  if(phase==0)return;
  if(phase==2){if(now()<nextCheck)return;
   actions.Queue(s,"local t=assert(_G['"+token+"'],'resultat absent'); assert(t.status~='queued','EB_BODY_PENDING'); local status=t.status; _G['"+token+"']=nil; assert(status=='completed',tostring(status))",false,true);
   phase=3;return;
  }
  uint result=actions.LastCompletionState;if(result==0)return;
  if(result!=3){if(phase==3&&result==4&&actions.LastStatus.Contains("EB_BODY_PENDING")&&retries++<8){phase=2;nextCheck=now().AddSeconds(1);return;}
   Idle("Véhicule : résultat non confirmé. "+actions.LastStatus);return;}
  if(phase==1){phase=2;nextCheck=now().AddSeconds(1.5);}
  else if(phase==3)Idle("Réparation du véhicule terminée (moteur + carrosserie). Tu peux repartir.");
 }
}
}
