using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace EmpireBay {
 enum PatchState { ExeNotFound, NotPe32, Original, Patched }
 // Patch « 4 Go » : active le drapeau PE IMAGE_FILE_LARGE_ADDRESS_AWARE (0x20) sur mafia2.exe (32 bits),
 // ce qui lui permet d'utiliser jusqu'à ~4 Go de mémoire et évite des crashes. Une seule modification de
 // deux octets dans l'en-tête PE, pas un crack. Détection par le drapeau lui-même (indépendante de la version).
 static class GamePatch {
  const ushort LargeAddressAware=0x0020;
  // Inspection PE pure, réutilisable en test sans toucher au disque.
  public static bool TryCharacteristicsOffset(byte[] pe,out int offset){
   offset=-1;
   if(pe==null||pe.Length<0x40||pe[0]!=(byte)'M'||pe[1]!=(byte)'Z')return false;
   int h=BitConverter.ToInt32(pe,0x3c);
   if(h<0||(long)h+24>pe.Length)return false;
   if(pe[h]!=(byte)'P'||pe[h+1]!=(byte)'E'||pe[h+2]!=0||pe[h+3]!=0)return false;
   if(BitConverter.ToUInt16(pe,h+4)!=0x14c)return false; // machine x86 : Mafia II Classic est 32 bits.
   offset=h+22;return true; // Characteristics = en-tête COFF (pe+4) + 18.
  }
  public static PatchState StateOf(byte[] pe){
   int o;if(!TryCharacteristicsOffset(pe,out o))return PatchState.NotPe32;
   return (BitConverter.ToUInt16(pe,o)&LargeAddressAware)!=0?PatchState.Patched:PatchState.Original;
  }
  // Pose le drapeau sur le tampon. Retourne l'offset modifié, ou -1 si déjà patché / PE non reconnu.
  public static int SetLargeAddressAware(byte[] pe){
   int o;if(!TryCharacteristicsOffset(pe,out o))return -1;
   ushort flags=BitConverter.ToUInt16(pe,o);
   if((flags&LargeAddressAware)!=0)return -1;
   byte[] nf=BitConverter.GetBytes((ushort)(flags|LargeAddressAware));
   pe[o]=nf[0];pe[o+1]=nf[1];return o;
  }
  public static bool GameRunning(){try{return Process.GetProcessesByName("mafia2").Length>0;}catch{return false;}}
  public static PatchState Inspect(string exePath){
   if(string.IsNullOrEmpty(exePath)||!File.Exists(exePath))return PatchState.ExeNotFound;
   try{return StateOf(File.ReadAllBytes(exePath));}catch{return PatchState.ExeNotFound;}
  }
  // Applique le patch : sauvegarde l'exe original dans le dossier du trainer, pose le drapeau, revérifie.
  // Retourne le chemin de la sauvegarde. Lève une exception claire en cas d'échec (jeu ouvert, accès refusé…).
  public static string Apply(string exePath){
   if(string.IsNullOrEmpty(exePath)||!File.Exists(exePath))throw new FileNotFoundException("mafia2.exe introuvable.");
   if(GameRunning())throw new InvalidOperationException("Ferme Mafia II avant d'installer le patch (le jeu ne doit pas tourner).");
   byte[] bytes=File.ReadAllBytes(exePath);
   int o;if(!TryCharacteristicsOffset(bytes,out o))throw new InvalidDataException("Ce fichier n'est pas un mafia2.exe 32 bits reconnu.");
   if((BitConverter.ToUInt16(bytes,o)&LargeAddressAware)!=0)throw new InvalidOperationException("Le patch 4 Go est déjà installé.");
   string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PatchBackup");Directory.CreateDirectory(dir);
   string backup=Path.Combine(dir,"mafia2.exe.original-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
   File.Copy(exePath,backup,false);
   int changed=SetLargeAddressAware(bytes);
   if(changed<0)throw new InvalidDataException("Drapeau introuvable : patch annulé.");
   try{
    using(var fs=new FileStream(exePath,FileMode.Open,FileAccess.ReadWrite))
    {fs.Seek(changed,SeekOrigin.Begin);fs.Write(bytes,changed,2);fs.Flush();}
   }catch(UnauthorizedAccessException){throw new UnauthorizedAccessException("Accès refusé à mafia2.exe. Relance le trainer en tant qu'administrateur, puis réessaie.");}
   if(StateOf(File.ReadAllBytes(exePath))!=PatchState.Patched)throw new IOException("Vérification du patch échouée : l'exe n'a pas été modifié.");
   return backup;
  }
  // Localise mafia2.exe : jeu lancé, puis dossiers Steam (registre + bibliothèques), puis chemin standard.
  public static string LocateExe(){
   try{var p=Process.GetProcessesByName("mafia2");if(p.Length>0){try{string f=p[0].MainModule.FileName;if(File.Exists(f))return f;}catch{}}}catch{}
   foreach(string steam in SteamRoots()){
    string c=Path.Combine(steam,"steamapps","common","Mafia II","pc","mafia2.exe");
    if(File.Exists(c))return c;
   }
   return "";
  }
  static List<string> SteamRoots(){
   var roots=new List<string>();
   Action<string> add=s=>{if(!string.IsNullOrEmpty(s)){s=s.Replace('/','\\');if(!roots.Contains(s))roots.Add(s);}};
   string reg=null;
   try{reg=Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam","SteamPath",null) as string;}catch{}
   if(string.IsNullOrEmpty(reg))try{reg=Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam","InstallPath",null) as string;}catch{}
   add(reg);
   add(@"C:\Program Files (x86)\Steam");
   // Bibliothèques Steam additionnelles (jeu sur un autre disque).
   foreach(string root in new List<string>(roots)){
    string vdf=Path.Combine(root,"steamapps","libraryfolders.vdf");
    try{if(File.Exists(vdf))foreach(Match m in Regex.Matches(File.ReadAllText(vdf),"\"path\"\\s*\"([^\"]+)\""))add(m.Groups[1].Value.Replace("\\\\","\\"));}catch{}
   }
   return roots;
  }
 }
 static class GamePatchTests {
  static void Check(bool v,string r){if(!v)throw new Exception(r);}
  public static void Run(){
   // Faux PE 32 bits minimal, sans drapeau LAA.
   byte[] pe=new byte[0x100];pe[0]=(byte)'M';pe[1]=(byte)'Z';int h=0x80;BitConverter.GetBytes(h).CopyTo(pe,0x3c);
   pe[h]=(byte)'P';pe[h+1]=(byte)'E';BitConverter.GetBytes((ushort)0x14c).CopyTo(pe,h+4);BitConverter.GetBytes((ushort)0x0102).CopyTo(pe,h+22);
   Check(GamePatch.StateOf(pe)==PatchState.Original,"PE sans LAA = Original");
   byte[] before=(byte[])pe.Clone();
   int o=GamePatch.SetLargeAddressAware(pe);
   Check(o==h+22,"Offset du drapeau LAA");
   int diff=0;for(int i=0;i<pe.Length;i++)if(pe[i]!=before[i])diff++;
   Check(diff==1,"Le patch ne change qu'un seul octet (le drapeau)");
   Check(GamePatch.StateOf(pe)==PatchState.Patched,"Après patch = Patched");
   Check(GamePatch.SetLargeAddressAware(pe)==-1,"Re-patch refusé si déjà LAA");
   Check(GamePatch.StateOf(new byte[]{1,2,3})==PatchState.NotPe32,"Tampon invalide = NotPe32");
  }
 }
}
