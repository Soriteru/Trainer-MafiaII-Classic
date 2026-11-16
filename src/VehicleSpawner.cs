using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace EmpireBay {
sealed class VehicleEntry {
 public uint Slot,Row,Model,SlotNumber;public string Name;
 public override string ToString(){return Name.Replace('_',' ') + " · modèle chargé";}
}
// Choix proposé dans la liste du spawner : un nom de voiture (du disque et/ou chargé en scène).
// Loaded!=null → apparition immédiate via le spawner natif ; sinon → tentative de chargement forcé.
sealed class CarChoice {
 public string Name;public VehicleEntry Loaded;
 public bool IsLoaded{get{return Loaded!=null;}}
 public override string ToString(){return Name.Replace('_',' ')+(IsLoaded?"":"  ·  non chargé — indisponible dans ce chapitre");}
}
// Creates a new car from a loaded model. Never replaces or unloads mission resources.
static class VehicleSpawner {
 public static bool Enabled {get{return true;}}
 // Ouvert à TOUS les modèles /sds/cars/ chargés dans la scène (demande joueur). Chaque modèle reste expérimental au spawn (non testé individuellement).
 public static bool Allowed(string name){return !string.IsNullOrEmpty(name);}
 // Noms de base des voitures présentes dans les fichiers du jeu (sds\cars\*.sds), tous chapitres confondus.
 // Sert à proposer le catalogue complet même pour les modèles non chargés dans la scène actuelle.
 public static List<string> DiskCars(string carsDir){
  var result=new List<string>();
  if(string.IsNullOrEmpty(carsDir)||!Directory.Exists(carsDir))return result;
  var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(string path in Directory.GetFiles(carsDir,"*.sds")){
   string n=Path.GetFileNameWithoutExtension(path);
   foreach(string suffix in new[]{"_z","_pha","_fmv","_destr"})if(n.EndsWith(suffix,StringComparison.OrdinalIgnoreCase)){n=n.Substring(0,n.Length-suffix.Length);break;}
   string low=n.ToLowerInvariant();
   if(low.Length==0||low.Contains("universal")||low=="hud"||low=="fuel_tank"||low.StartsWith("player_")||low.EndsWith("_textures"))continue;
   set.Add(n);
  }
  return set.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToList();
 }
 public static List<VehicleEntry> Catalogue(IMemory memory,uint image){
  uint manager=Values.Ptr(memory,image+0x16bfe3c),start=Values.Ptr(memory,manager+0x20),end=Values.U(memory,manager+0x24);
  if(end<start||(end-start)%4!=0||(end-start)/4>256)throw new InvalidDataException("Slots non reconnus.");
  var cars=new List<VehicleEntry>();
  for(uint i=start;i<end;i+=4){uint slot=Values.Ptr(memory,i);if(Values.U(memory,slot+0x40)!=11)continue;
   string path=Encoding.ASCII.GetString(memory.Read(slot+0x74,128)).Split('\0')[0];if(!path.StartsWith("/sds/cars/",StringComparison.OrdinalIgnoreCase))continue;
   uint rows=Values.U(memory,slot+0x120),finish=Values.U(memory,slot+0x124);if(rows<0x10000||finish<rows||(finish-rows)%24!=0||(finish-rows)/24>256)continue;
   string file=Path.GetFileNameWithoutExtension(path);foreach(string suffix in new[]{"_z","_pha","_fmv"})if(file.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))file=file.Substring(0,file.Length-suffix.Length);
   for(uint row=rows;row<finish;row+=24){uint model=Values.U(memory,row),label=Values.U(memory,row+12);if(model<0x10000||label<0x10000)continue;
    string name=Encoding.ASCII.GetString(memory.Read(label,64)).Split('\0')[0];if(!string.Equals(name,file,StringComparison.OrdinalIgnoreCase))continue;
    cars.Add(new VehicleEntry{Slot=slot,Row=row,Model=model,SlotNumber=Values.U(memory,slot+0x1a8),Name=name});
   }
  }
  return cars.Where(v=>Allowed(v.Name)).GroupBy(v=>v.Name,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).OrderBy(v=>v.Name).ToList();
 }
 public static void Validate(IMemory m,uint image,VehicleEntry v){
  if(!Catalogue(m,image).Any(x=>x.Slot==v.Slot&&x.Row==v.Row&&x.Model==v.Model&&x.SlotNumber==v.SlotNumber))throw new InvalidOperationException("Ce modèle n’est plus chargé. Relis la liste.");
 }
 public static void Prepare(IPatchMemory m,uint b,uint image,uint player,VehicleEntry v){
  m.Write(b+8,BitConverter.GetBytes(player));m.Write(b+12,BitConverter.GetBytes(v.Slot));m.Write(b+16,BitConverter.GetBytes(v.Row));m.Write(b+20,BitConverter.GetBytes(v.Model));m.Write(b+28,BitConverter.GetBytes(v.SlotNumber));
  m.Write(b+0x60,BitConverter.GetBytes(8f));m.Write(b+0x64,BitConverter.GetBytes(.3f));m.Write(b+0x100,Encoding.ASCII.GetBytes("EB_"+Guid.NewGuid().ToString("N")+"\0"));m.Write(b+0x500,Code(b,image));
 }
 public static byte[] Code(uint b,uint image){
  var a=new X86();uint code=b+0x500;
  Action<uint> call=address=>{a.Emit(0xe8);a.Number(unchecked(image+address-0x400000-(code+(uint)a.Code.Count+4)));};
  Action<uint,uint> store=(offset,value)=>{a.Emit(0xc7,0x05);a.Number(b+offset);a.Number(value);};
  Action<uint> loadThis=offset=>{a.Emit(0x8b,0x0d);a.Number(b+offset);};
  Action<uint> pushPtr=offset=>{a.Emit(0x68);a.Number(b+offset);};
  Action<uint> saveEax=offset=>{a.Emit(0xa3);a.Number(b+offset);};
  a.Emit(0x60);store(4,1);
  loadThis(8);a.Emit(0x83,0x79,0x64,0);a.Jump(0x85,"fail");
  loadThis(12);a.Emit(0xa1);a.Number(b+16);a.Emit(0x3b,0x81,0x20,1,0,0);a.Jump(0x82,"fail");a.Emit(0x3b,0x81,0x24,1,0,0);a.Jump(0x83,"fail");a.Emit(0x8b,0x00,0x3b,0x05);a.Number(b+20);a.Jump(0x85,"fail");
  loadThis(12);a.Emit(0x8b,0x81,0xa8,1,0,0,0x3b,0x05);a.Number(b+28);a.Jump(0x85,"fail");
  a.Emit(0x6a,0);call(0x598f40);a.Emit(0x83,0xf8,5);a.Jump(0x85,"fail");
  loadThis(20);a.Emit(0x8b,0x01,0xff,0x50,0x18,0x83,0xf8,2);a.Jump(0x85,"fail");
  // Capture current location and heading on the game thread.
  loadThis(8);pushPtr(0x40);a.Emit(0x8b,0x01,0xff,0x90,0x8c,0,0,0);
  loadThis(8);pushPtr(0x50);a.Emit(0x8b,0x01,0xff,0x90,0x90,0,0,0);
  for(uint off=0;off<8;off+=4){a.Emit(0xd9,0x05);a.Number(b+0x50+off);a.Emit(0xd8,0x0d);a.Number(b+0x60);a.Emit(0xd8,0x05);a.Number(b+0x40+off);a.Emit(0xd9,0x1d);a.Number(b+0x40+off);}
  a.Emit(0xd9,0x05);a.Number(b+0x48);a.Emit(0xd8,0x05);a.Number(b+0x64);a.Emit(0xd9,0x1d);a.Number(b+0x48);
  store(4,2);a.Emit(0x6a,0x18);call(0x401830);a.Emit(0x83,0xc4,4,0x85,0xc0);a.Jump(0x84,"fail");saveEax(32);a.Emit(0x8b,0xc8);call(0x599c50);
  a.Emit(0xff,0x35);a.Number(b+12);loadThis(32);call(0x5c3950); // Hold a reference to the source slot.
  store(4,3);a.Emit(0x6a,0x12);call(0x1167a60);a.Emit(0x83,0xc4,4,0x85,0xc0);a.Jump(0x84,"failSlot");saveEax(24);a.Emit(0x8b,0x00,0x81,0x78,0x0c);a.Number(image+0x6151f0);a.Jump(0x85,"failCar");
  a.Emit(0x6a,2,0x8b,0x0d);a.Number(image+0x16c2778);call(0x14ebfb0);a.Emit(0x85,0xc0);a.Jump(0x84,"failCar");saveEax(36);
  a.Emit(0xff,0x35);a.Number(b+20);a.Emit(0x8b,0xc8);call(0x14ec8f0);
  loadThis(36);pushPtr(0x100);call(0x14ba350);loadThis(36);a.Emit(0x6a,2);call(0x14ba3d0);
  a.Emit(0xff,0x35);a.Number(b+36);loadThis(24);a.Emit(0x8b,0x01,0xff,0x90,0xb0,0,0,0);
  store(4,4);a.Emit(0x6a,0);loadThis(24);call(0x9a1e40);a.Emit(0x84,0xc0);a.Jump(0x84,"failCar");
  loadThis(24);a.Emit(0xa1);a.Number(b+28);a.Emit(0x89,0x41,0x78);a.Emit(0x8b,0x01,0xff,0x50,0x0c); // Use the vehicle virtual Setup, never the hard-coded Human setup.
  store(4,5);loadThis(24);a.Emit(0x81,0x61,0x20,0xbf,0xff,0xff,0xff,0x81,0x49,0x20,0,0x48,0,0,0xf6,0x41,0x20,0x20);a.Jump(0x84,"failCar");
  pushPtr(0x40);call(0x46ca90);loadThis(24);pushPtr(0x50);call(0x46caf0);loadThis(24);call(0x1192170);loadThis(24);call(0x11665a0);a.Emit(0x85,0xc0);a.Jump(0x84,"failCar");
  store(4,6);a.Emit(0x61,0x31,0xc0,0xc3);
  a.Label("failCar");loadThis(24);call(0x119ddd0);store(24,0);
  a.Label("failSlot");loadThis(32);call(0x5ad300);
  a.Label("fail");a.Emit(0x61,0xb8);a.Number(0x53504157);a.Emit(0xc3);return a.Finish();
 }
}
}
