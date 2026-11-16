using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
namespace EmpireBay {
interface IPatchMemory:IMemory { uint Allocate(int count); void Patch(uint address,byte[] value); }
sealed class X86 {
 public readonly List<byte> Code=new List<byte>();
 readonly Dictionary<string,int> labels=new Dictionary<string,int>();
 readonly List<Tuple<int,string>> branches=new List<Tuple<int,string>>();
 public void Emit(params byte[] bytes){Code.AddRange(bytes);}
 public void Number(uint value){Emit(BitConverter.GetBytes(value));}
 public void Label(string label){labels[label]=Code.Count;}
 public void Jump(byte condition,string label){Emit(0x0f,condition);branches.Add(Tuple.Create(Code.Count,label));Number(0);}
 public void JumpAlways(string label){Emit(0xe9);branches.Add(Tuple.Create(Code.Count,label));Number(0);}
 public void Tail(uint destination,uint baseAddress){Emit(0xe9);Number(unchecked(destination-(baseAddress+(uint)Code.Count+4)));}
 public byte[] Finish(){foreach(var b in branches){byte[] delta=BitConverter.GetBytes(labels[b.Item2]-b.Item1-4);for(int i=0;i<4;i++)Code[b.Item1+i]=delta[i];}return Code.ToArray();}
}
sealed class PlayerHooks:IDisposable {
 readonly IPatchMemory memory;readonly uint root,imageBase;
 uint cave;bool ammoInstalled,recoilInstalled,precisionInstalled;uint lastPlayer,lastInventory;bool lastReload,lastRecoil,lastPrecision;
 readonly uint[] recoilBounds=new uint[2];bool rowsConfigured;
 public string RestoreError{get;private set;}="";
 public readonly List<Tuple<uint,byte[],byte[]>> Patches=new List<Tuple<uint,byte[],byte[]>>();
 public PlayerHooks(IPatchMemory m,uint singleton,uint image){memory=m;root=singleton;imageBase=image;}
 public uint FlagAddress{get{return cave;}}
 public void ConfigureValidatedRecoilRows(){
  if(rowsConfigured)return;
  uint tables=Values.Ptr(memory,imageBase+0x16b5a60),wrapper=Values.Ptr(memory,tables+0x20),table=Values.Ptr(memory,wrapper+4);
  uint count=Values.U(memory,table+8),stride=Values.U(memory,table+0x14),rows=Values.Ptr(memory,table+0x1c);
  if(count<1||count>512||stride<0x28||stride>4096)throw new InvalidDataException("Table des armes non reconnue : sans recul refusé.");
  // Universel : toute arme dont la ligne appartient à la table du jeu. Les bornes [début, fin) remplacent la liste de trois identités.
  uint end=checked(rows+count*stride);
  recoilBounds[0]=rows;recoilBounds[1]=end;rowsConfigured=true;
  if(cave!=0){memory.Write(cave+12,BitConverter.GetBytes(recoilBounds[0]));memory.Write(cave+16,BitConverter.GetBytes(recoilBounds[1]));}
 }
 static void ValidatedRowGuard(X86 a,uint bounds){
  // Apply only to a genuine entry inside the game weapon table owned by the player. Never dereference an unknown/current weapon row.
  a.Emit(0x8b,0x81,0xa8,0x01,0,0,0x85,0xc0);a.Jump(0x84,"normal"); // mov eax,[ecx+0x1a8]; test eax,eax; jz normal
  a.Emit(0x3b,0x05);a.Number(bounds);a.Jump(0x82,"normal");        // cmp eax,[rows start]; jb normal
  a.Emit(0x3b,0x05);a.Number(bounds+4);a.Jump(0x83,"normal");      // cmp eax,[rows end];   jae normal
  a.Emit(0x8b,0x10,0x83,0xfa,0x0e);a.Jump(0x84,"normal");          // mov edx,[eax]; cmp edx,14; jz normal — exclure la MG42 (ID 14) : crash reproduit chapitre 5.
 }
 public static byte[] AmmoCode(uint root,uint flag,uint cave,uint resume){
  var a=new X86();a.Emit(0x9c,0x60); // Preserve flags and all registers.
  a.Emit(0x80,0x3d);a.Number(flag);a.Emit(0);a.Jump(0x84,"normal");
  // Compare identities cached by the trainer. Never dereference a scene/player pointer here.
  a.Emit(0xa1);a.Number(flag+8);a.Emit(0x85,0xc0);a.Jump(0x84,"normal");
  a.Emit(0x8b,0x51,0x04,0x85,0xd2);a.Jump(0x84,"normal"); // mov edx,[ecx+4]; test edx,edx; jz normal
  a.Emit(0x81,0xfa,0,0,1,0);a.Jump(0x82,"normal");        // cmp edx,0x10000; jb normal — pointeur plausible avant déréférencement
  a.Emit(0x3b,0x42,0x08);a.Jump(0x85,"normal"); // cmp eax,[edx+8]; jne normal — référence appartient à l'inventaire local.
  a.Emit(0x8b,0x51,0x08,0x85,0xd2);a.Jump(0x84,"normal");  // mov edx,[ecx+8]; test edx,edx; jz normal — MANQUAIT : crash au ramassage du fusil à pompe (0x5A46E0, 27/10).
  a.Emit(0x81,0xfa,0,0,1,0);a.Jump(0x82,"normal");        // cmp edx,0x10000; jb normal — pointeur plausible avant déréférencement
  a.Emit(0x81,0x7a,0x04);a.Number(1000);a.Jump(0x83,"normal"); // cmp [edx+4],1000; jae normal — armes, pas conteneurs de réserve.
  a.Emit(0x83,0x7c,0x24,0x28,0);a.Jump(0x89,"normal"); // Original stack argument: only block negative deltas.
  a.Emit(0x61,0x9d,0x8b,0x41,0x08,0x8b,0x54,0x24,0x04);
  a.Tail(resume+3,cave); // Skip only ADD [eax+0xc],edx; retain game notifications.
  a.Label("normal");a.Emit(0x61,0x9d,0x8b,0x41,0x08,0x8b,0x54,0x24,0x04);a.Tail(resume,cave);
  return a.Finish();
 }
 public static byte[] RecoilCode(uint root,uint flag,uint cave,uint resume){
  var a=new X86();a.Emit(0x9c,0x60);
  a.Emit(0x80,0x3d);a.Number(flag);a.Emit(0);a.Jump(0x84,"normal");
  a.Emit(0xa1);a.Number(flag+3);a.Emit(0x85,0xc0);a.Jump(0x84,"normal");
  a.Emit(0x39,0x81,0x74,0x01,0,0);a.Jump(0x85,"normal"); // Weapon controller owner is local player.
  ValidatedRowGuard(a,flag+11);
  a.Emit(0x61,0x9d,0x31,0xc0,0x89,0x41,0x50,0x89,0x41,0x54,0x89,0x41,0x58,0xc3);
  a.Label("normal");a.Emit(0x61,0x9d,0x53,0x56,0x57,0x8b,0xf1);a.Tail(resume,cave);
  return a.Finish();
 }
 void Add(uint address,byte[] original,uint codeAddress,byte[] code){
  byte[] cur=memory.Read(address,original.Length);
  // Auto-réparation : si la routine n'est pas d'origine mais commence par notre saut (0xE9), c'est un reste d'un hook précédent dont la restauration a échoué (ex. pendant un tir). Il est inerte (flag à 0) ; on réinstalle proprement par-dessus au lieu de refuser. On refuse seulement un contenu vraiment inconnu (autre outil).
  if(!cur.SequenceEqual(original)&&cur[0]!=0xe9)throw new InvalidDataException("Routine du jeu différente : modification refusée.");
  memory.Write(codeAddress,code);
  var patch=new byte[original.Length];for(int i=0;i<patch.Length;i++)patch[i]=0x90;
  patch[0]=0xe9;Array.Copy(BitConverter.GetBytes(unchecked(codeAddress-address-5)),0,patch,1,4);
  memory.Patch(address,patch);Patches.Add(Tuple.Create(address,original,patch));
 }
 public static byte[] PrecisionCode(uint flag,uint cave,uint resume){
  var a=new X86();a.Emit(0x9c,0x60,0x80,0x3d);a.Number(flag);a.Emit(0);a.Jump(0x84,"normal");
  a.Emit(0xa1);a.Number(flag+2);a.Emit(0x85,0xc0);a.Jump(0x84,"normal");a.Emit(0x39,0x81,0x74,0x01,0,0);a.Jump(0x85,"normal");
  ValidatedRowGuard(a,flag+10);
  a.Emit(0xc7,0x81,0x58,0x01,0,0,0,0,0,0);
  a.Label("normal");a.Emit(0x61,0x9d,0x83,0xec,0x2c,0x53,0x56);a.Tail(resume,cave);return a.Finish();
 }
 public void Set(bool noReload,bool noRecoil,uint player=0,uint inventory=0,bool precision=false){
  // Chaque routine du jeu n'est modifiée que si l'option correspondante est demandée : le sans-recul seul n'installe plus le hook munitions.
  if((noReload||noRecoil)&&cave==0){
   cave=memory.Allocate(4096);memory.Write(cave,new byte[24]);memory.Write(cave+12,BitConverter.GetBytes(recoilBounds[0]));memory.Write(cave+16,BitConverter.GetBytes(recoilBounds[1]));
  }
  try{
   if(noReload&&!ammoInstalled){uint ammo=imageBase+0x5a46e0;Add(ammo,new byte[]{0x8b,0x41,0x08,0x8b,0x54,0x24,0x04},cave+0x100,AmmoCode(root,cave,cave+0x100,ammo+7));ammoInstalled=true;}
   if(noRecoil&&!recoilInstalled){uint recoil=imageBase+0x20e60;Add(recoil,new byte[]{0x53,0x56,0x57,0x8b,0xf1},cave+0x300,RecoilCode(root,cave+1,cave+0x300,recoil+5));recoilInstalled=true;}
   if(noRecoil&&precision&&!precisionInstalled){Add(imageBase+0x208b0,new byte[]{0x83,0xec,0x2c,0x53,0x56},cave+0x500,PrecisionCode(cave+2,cave+0x500,imageBase+0x208b5));precisionInstalled=true;}
  }catch{Dispose();throw;}
  if(cave!=0){
   if(lastPlayer!=player||lastInventory!=inventory){
    memory.Write(cave,new byte[3]);memory.Write(cave+4,BitConverter.GetBytes(player));memory.Write(cave+8,BitConverter.GetBytes(inventory));
    lastPlayer=player;lastInventory=inventory;lastReload=lastRecoil=lastPrecision=false;
   }
   if(lastReload!=noReload||lastRecoil!=noRecoil||lastPrecision!=precision){memory.Write(cave,new byte[]{(byte)(noReload?1:0),(byte)(noRecoil?1:0),(byte)(precision?1:0)});lastReload=noReload;lastRecoil=noRecoil;lastPrecision=precision;}
  }
 }
 public void Dispose(){
  if(cave!=0){try{memory.Write(cave,new byte[3]);}catch{}}
  foreach(var p in Patches.AsEnumerable().Reverse()){
   for(int attempt=0;attempt<5;attempt++){
    try{byte[] current=memory.Read(p.Item1,p.Item3.Length);if(current.SequenceEqual(p.Item2))break;
     if(!current.SequenceEqual(p.Item3))throw new IOException("Routine modifiée par un autre outil. Restauration refusée.");
     memory.Patch(p.Item1,p.Item2);break;
    }catch(Exception ex){if(attempt==4)RestoreError=ex.Message;else System.Threading.Thread.Sleep(2);}
   }
  }
  Patches.Clear();ammoInstalled=recoilInstalled=precisionInstalled=false;
  // Dispose a remis les drapeaux du cave à 0 : on invalide AUSSI le cache pour que la prochaine activation (après une cinématique/MG42) réécrive bien joueur/inventaire ET les drapeaux, sinon les hooks seraient réinstallés mais inertes (bouton allumé sans effet).
  lastPlayer=lastInventory=0;lastReload=lastRecoil=lastPrecision=false;
  // Keep the small cave mapped until the game exits: a game thread can still be returning from it.
 }
 // Full on-demand removal: restores the original game routines and lets a later activation reinstall cleanly.
 public void Uninstall(){
  Dispose();
  cave=0;rowsConfigured=false;
  lastPlayer=lastInventory=0;lastReload=lastRecoil=lastPrecision=false;
 }
}
}
