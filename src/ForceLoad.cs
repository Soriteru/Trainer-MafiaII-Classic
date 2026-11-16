using System;
using System.Collections.Generic;
using System.Linq;
namespace EmpireBay {
// Chargement forcé d'un modèle de véhicule absent de la scène, puis apparition automatique.
// Orchestré tick par tick AU-DESSUS de LuaActions : aucune nouvelle routine injectée.
//  1. On essaie SdsLoadBuffer:SdsLoad("<nom>") avec PLUSIEURS formats de nom à la suite
//     (/sds/cars/x, sds/cars/x, cars/x, x) car la signature exacte attendue est inconnue.
//  2. Après chaque essai on re-scanne le catalogue des modèles réellement chargés.
//     Dès que le modèle demandé y apparaît (preuve que le SDS est monté), on déclenche
//     le spawner natif existant (déjà validé). Le format qui a marché est mémorisé/affiché.
//  3. Si aucun format ne fait apparaître le modèle avant la fin du délai → échec explicite
//     (= SdsLoad ne sait pas charger ce modèle à la volée ; repli sur le spawn des chargés).
sealed class ForceLoad {
 readonly LuaActions actions;
 enum Phase{Idle,Requesting,Loading,Spawning,Done,Failed}
 Phase phase=Phase.Idle;string baseName="",status="Chargement forcé inactif.",loadedVia="";
 string[] candidates=new string[0];int ci;DateTime deadline,candidateDeadline,lastScan=DateTime.MinValue;
 public ForceLoad(LuaActions a){actions=a;}
 public bool Active{get{return phase==Phase.Requesting||phase==Phase.Loading||phase==Phase.Spawning;}}
 public string Status{get{return status;}}
 // Nom de fichier nu (ce que contient le catalogue) à partir d'un chemin SDS « cars/hot_rod_2 ».
 public static string BaseName(string sds){
  string s=(sds??"").Trim().Replace('\\','/');int slash=s.LastIndexOf('/');if(slash>=0)s=s.Substring(slash+1);
  int dot=s.IndexOf('.');if(dot>=0)s=s.Substring(0,dot);
  foreach(string suffix in new[]{"_z","_pha","_fmv","_destr"})if(s.EndsWith(suffix,StringComparison.OrdinalIgnoreCase)){s=s.Substring(0,s.Length-suffix.Length);break;}
  return s;
 }
 public void Request(string name){
  name=(name??"").Trim();
  if(name.Length==0||name.Length>80||!name.All(c=>char.IsLetterOrDigit(c)||c=='/'||c=='_'||c=='-'))throw new InvalidOperationException("Nom SDS invalide (lettres, chiffres, / _ -).");
  if(Active)throw new InvalidOperationException("Un chargement forcé est déjà en cours.");
  baseName=BaseName(name);
  if(baseName.Length==0)throw new InvalidOperationException("Nom SDS invalide.");
  // Formats candidats, du plus probable (chemin interne observé dans les slots) au plus simple.
  var list=new List<string>();
  if(name.Contains("/"))list.Add(name); // honore un chemin saisi manuellement
  list.Add("/sds/cars/"+baseName);list.Add("sds/cars/"+baseName);list.Add("cars/"+baseName);list.Add(baseName);
  candidates=list.Distinct().ToArray();ci=0;loadedVia="";lastScan=DateTime.MinValue;
  phase=Phase.Requesting;deadline=DateTime.UtcNow.AddSeconds(8*candidates.Length+6);
  status="Chargement forcé demandé : "+baseName+".";
 }
 public void Cancel(){if(Active){phase=Phase.Idle;status="Chargement forcé annulé.";}}
 // Scan borné (3/s max) du catalogue des modèles chargés ; renvoie l'entrée correspondant au nom demandé.
 VehicleEntry Find(){
  if((DateTime.UtcNow-lastScan).TotalMilliseconds<330)return null;lastScan=DateTime.UtcNow;
  try{return actions.Vehicles().FirstOrDefault(v=>string.Equals(v.Name,baseName,StringComparison.OrdinalIgnoreCase));}catch{return null;}
 }
 public void Tick(Snapshot s){
  if(!Active)return;
  if(DateTime.UtcNow>deadline){phase=Phase.Failed;status="Chargement forcé : « "+baseName+" » n'est jamais apparu après "+candidates.Length+" format(s) de nom. SdsLoad ne sait pas charger ce modèle à la volée — reste sur les véhicules déjà chargés.";return;}
  uint state=actions.ExecutionState; // 1/2 = canal occupé, 3 = terminé OK, 4 = erreur
  switch(phase){
   case Phase.Requesting:
    var ready=Find(); // déjà chargé dans la scène ? apparition directe.
    if(ready!=null){loadedVia="(déjà chargé)";QueueSpawn(s,ready);return;}
    if(state==1||state==2)return; // attendre que le canal Lua se libère
    if(ci>=candidates.Length){phase=Phase.Failed;status="Chargement forcé : aucun format de nom n'a monté « "+baseName+" ».";return;}
    try{actions.Queue(s,"SdsLoadBuffer:SdsLoad(\""+candidates[ci]+"\")",false);phase=Phase.Loading;candidateDeadline=DateTime.UtcNow.AddSeconds(7);status="Chargement SDS (format "+(ci+1)+"/"+candidates.Length+" : "+candidates[ci]+")…";}
    catch(Exception e){phase=Phase.Failed;status="Chargement forcé refusé : "+e.Message;}
    return;
   case Phase.Loading:
    var found=Find(); // le modèle est-il arrivé en mémoire ?
    if(found!=null){loadedVia=candidates[ci];QueueSpawn(s,found);return;}
    if(DateTime.UtcNow>candidateDeadline&&(state==3||state==4||state==0)){ci++;phase=Phase.Requesting;} // format suivant
    return;
   case Phase.Spawning:
    if(state==1||state==2){status="Modèle « "+baseName+" » chargé ; apparition en cours…";return;}
    if(actions.LastCompletionState==3){phase=Phase.Done;status="Véhicule « "+baseName+" » chargé "+loadedVia+" puis créé devant Vito.";}
    else if(actions.LastCompletionState==4){phase=Phase.Failed;status="Modèle chargé "+loadedVia+" mais échec à l'apparition. "+actions.LastStatus;}
    return;
  }
 }
 void QueueSpawn(Snapshot s,VehicleEntry entry){
  if(actions.ExecutionState==1||actions.ExecutionState==2)return; // canal encore occupé par le SdsLoad
  if(s.Car!=0){status="Sors du véhicule : « "+baseName+" » est chargé "+loadedVia+" et prêt à apparaître.";return;}
  try{actions.Spawn(s,entry);phase=Phase.Spawning;status="Modèle « "+baseName+" » chargé "+loadedVia+" ; apparition en cours…";}
  catch(Exception e){phase=Phase.Failed;status="Modèle chargé "+loadedVia+" mais apparition refusée : "+e.Message;}
 }
}
}
