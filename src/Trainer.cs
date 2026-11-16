using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace EmpireBay {
interface IMemory { byte[] Read(uint address,int count); void Write(uint address,byte[] value); }
static class Values {
 public static uint U(IMemory m,uint a){return BitConverter.ToUInt32(m.Read(a,4),0);}
 public static float F(IMemory m,uint a){return BitConverter.ToSingle(m.Read(a,4),0);}
 public static uint Ptr(IMemory m,uint a){uint p=U(m,a);if(p<0x10000 || p>0xfffeffff)throw new InvalidDataException("Structure absente : chargement ou menu.");return p;}
}
sealed class NativeMemory:IPatchMemory,IDisposable {
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint rights,bool inherit,int pid);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadProcessMemory(IntPtr h,IntPtr a,byte[] b,UIntPtr n,out UIntPtr got);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool WriteProcessMemory(IntPtr h,IntPtr a,byte[] b,UIntPtr n,out UIntPtr got);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr VirtualAllocEx(IntPtr h,IntPtr a,UIntPtr size,uint allocation,uint protect);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool VirtualProtectEx(IntPtr h,IntPtr a,UIntPtr size,uint protect,out uint previous);
 [DllImport("kernel32.dll")] static extern bool FlushInstructionCache(IntPtr h,IntPtr a,UIntPtr size);
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenThread(uint rights,bool inherit,uint id);
 [DllImport("kernel32.dll")] static extern uint SuspendThread(IntPtr thread);
 [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool Wow64GetThreadContext(IntPtr thread,byte[] context);
 IntPtr handle;readonly int processId;public long ReadCalls,WriteCalls;
 public NativeMemory(int pid){processId=pid;handle=OpenProcess(0x438,false,pid);if(handle==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
 public byte[] Read(uint a,int n){return ReadAddress(a,n);}
 public void Write(uint a,byte[] b){WriteAddress(a,b);}
 internal byte[] ReadAddress(long a,int n){ReadCalls++;var b=new byte[n];UIntPtr got;if(!ReadProcessMemory(handle,new IntPtr(a),b,(UIntPtr)n,out got)||got.ToUInt64()!=(ulong)n)throw new IOException("Lecture mémoire interrompue.");return b;}
 internal void WriteAddress(long a,byte[] b){WriteCalls++;UIntPtr got;if(!WriteProcessMemory(handle,new IntPtr(a),b,(UIntPtr)b.Length,out got)||got.ToUInt64()!=(ulong)b.Length)throw new IOException("Écriture mémoire interrompue.");}
 public uint Allocate(int count){
  // Le cave contient du code x86 atteint par saut relatif + adressage absolu [disp32] : il fonctionne dans tout l'espace utilisateur, y compris > 2 Go sur l'exe patché 4 Go (LargeAddressAware). On accepte donc toute adresse utilisateur valide, comme Values.Ptr (0x10000..0xFFFEFFFF), au lieu de rejeter la mémoire haute.
  IntPtr a=VirtualAllocEx(handle,IntPtr.Zero,(UIntPtr)count,0x3000,0x40);long v=a.ToInt64();
  if(a==IntPtr.Zero||v<0x10000||v>0xfffeffff)throw new IOException("Allocation du module solo impossible.");
  return (uint)v;
 }
 public void Patch(uint a,byte[] bytes){
  var suspended=new List<IntPtr>();
  try{
   using(var process=Process.GetProcessById(processId))foreach(ProcessThread thread in process.Threads){
    IntPtr h=OpenThread(0x4a,false,(uint)thread.Id);if(h==IntPtr.Zero)throw new IOException("Le jeu change de threads : réessaie l'activation.");
    if(SuspendThread(h)==uint.MaxValue){CloseHandle(h);throw new IOException("Le jeu est en transition : réessaie l'activation.");}suspended.Add(h);
    byte[] ctx=new byte[716];Array.Copy(BitConverter.GetBytes(0x10001),ctx,4);
    if(!Wow64GetThreadContext(h,ctx))throw new IOException("Impossible de vérifier la routine en cours.");
    uint ip=BitConverter.ToUInt32(ctx,184);if(ip>=a&&ip<a+bytes.Length)throw new IOException("Cette routine est en cours d'exécution : réessaie l'activation.");
   }
   uint old;if(!VirtualProtectEx(handle,new IntPtr(a),(UIntPtr)bytes.Length,0x40,out old))throw new IOException("Routine protégée : activation impossible.");
   try{Write(a,bytes);FlushInstructionCache(handle,new IntPtr(a),(UIntPtr)bytes.Length);}finally{uint ignored;VirtualProtectEx(handle,new IntPtr(a),(UIntPtr)bytes.Length,old,out ignored);}
  }finally{foreach(IntPtr h in suspended){ResumeThread(h);CloseHandle(h);}}
 }
 public void Dispose(){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
}
sealed class Snapshot {
 public uint Player,Inventory,Wallet,WalletReference,WalletItem,Car;
 public float Health,MaxHealth;
 public long Cents;
 public readonly List<uint> Clips=new List<uint>(),Reserves=new List<uint>();
}
sealed class Cheats {
 readonly IMemory memory; readonly uint root;
 public VehicleScalars VehicleFuel{get{return new VehicleScalars(memory,root);}}
 public string WeaponDiagnostic(Snapshot s){try{uint c=Values.Ptr(memory,s.Player+0xb0);if(Values.U(memory,c+0x174)!=s.Player)return "owner inconnu";uint row=Values.U(memory,c+0x1a8);uint table=Values.Ptr(memory,Values.Ptr(memory,Values.Ptr(memory,root-0x16bfe14+0x16b5a60)+0x20)+4),rows=Values.Ptr(memory,table+0x1c),count=Values.U(memory,table+8),stride=Values.U(memory,table+0x14);if(count<1||count>512||stride<0x28||stride>4096||row<rows||(ulong)row>=(ulong)rows+count*stride||(row-rows)%stride!=0)return "row non reconnue "+row.ToString("X8");return "ID="+Values.U(memory,row)+" controller="+c.ToString("X8");}catch{return "arme indisponible";}}
 // Identifiant de l'arme équipée, ou 0 si indisponible/non reconnu. Sert à retirer les hooks tant que la MG42 fixe (ID 14) est tenue.
 uint CurrentWeaponId(Snapshot s){try{uint c=Values.Ptr(memory,s.Player+0xb0);if(Values.U(memory,c+0x174)!=s.Player)return 0;uint row=Values.U(memory,c+0x1a8);uint table=Values.Ptr(memory,Values.Ptr(memory,Values.Ptr(memory,root-0x16bfe14+0x16b5a60)+0x20)+4),rows=Values.Ptr(memory,table+0x1c),count=Values.U(memory,table+8),stride=Values.U(memory,table+0x14);if(count<1||count>512||stride<0x28||stride>4096||row<rows||(ulong)row>=(ulong)rows+(ulong)count*stride||(row-rows)%stride!=0)return 0;return Values.U(memory,row);}catch{return 0;}}
 public bool God,Ammo,NoReload,Money,NoRecoil;
 public LuaActions Actions;public PoliceProtection Protection;
 public BodyRepair Body;public ForceLoad Force;
 readonly PlayerHooks hooks;
 readonly Dictionary<uint,byte> godOriginal=new Dictionary<uint,byte>();
 readonly Dictionary<uint,byte> ammoOriginal=new Dictionary<uint,byte>();
 DateTime mg42Until=DateTime.MinValue;
 DateTime noControlUntil=DateTime.MinValue;
 // Vito est-il en contrôle normal ? Drapeau joueur +0x22 bit 0x20 : mis = pas en contrôle (cinématique/ascenseur/chargement), retiré = en contrôle. Validé en jeu le 26/10 : 0 en jeu normal ET en fusillade, 1 en cinématique. (Le candidat jeu +0x131 bit 0 a été écarté : il vaut aussi 0 en combat → faux positif qui coupait les cheats en fusillade.) Lecture impossible → considéré hors contrôle (sûr).
 bool InControl(Snapshot s){try{return (memory.Read(s.Player+0x22,1)[0]&0x20)==0;}catch{return false;}}
 public Cheats(IMemory m,uint r,uint image=0x400000){memory=m;root=r;if(m is IPatchMemory){hooks=new PlayerHooks((IPatchMemory)m,r,image);Actions=new LuaActions((IPatchMemory)m,image,r);Protection=new PoliceProtection(m,Actions,image);Body=new BodyRepair(m,Actions);Force=new ForceLoad(Actions);}}
 public Snapshot Inspect(){
  uint game=Values.Ptr(memory,root),player=Values.Ptr(memory,game+0x118),inv=Values.Ptr(memory,player+0xa0);
  uint script=Values.Ptr(memory,player+0xa4);
  if(Values.Ptr(memory,script)!=player)throw new InvalidDataException("Joueur non reconnu.");
  var s=new Snapshot{Player=player,Inventory=inv,Health=Values.F(memory,player+0xd0),MaxHealth=Values.F(memory,player+0xd4),Car=Values.U(memory,player+0x64)};
  if(float.IsNaN(s.Health)||float.IsInfinity(s.Health)||s.MaxHealth<1||s.MaxHealth>100000||s.Health<0||s.Health>s.MaxHealth*4)throw new InvalidDataException("Santé non reconnue.");
  uint slots=Values.Ptr(memory,inv+0x38);uint end=Values.U(memory,inv+0x3c);
  if(end<slots+24||end>slots+128)throw new InvalidDataException("Inventaire non reconnu.");
  for(int slot=0;slot<=5;slot++){
   uint bag=Values.Ptr(memory,slots+(uint)slot*4),start=Values.U(memory,bag+0x14),finish=Values.U(memory,bag+0x18);
   if(start==finish)continue;
   if(start<0x10000||finish<start||(finish-start)%4!=0||finish-start>1024)throw new InvalidDataException("Liste d'objets non reconnue.");
   for(uint a=start;a<finish;a+=4){
    uint reference=Values.Ptr(memory,a),item=Values.Ptr(memory,reference+0xc),data=Values.Ptr(memory,item+8);
    if(slot==5){
     if(Values.U(memory,data+4)!=2000||Values.Ptr(memory,item+4)!=reference||Values.Ptr(memory,reference+8)!=inv)throw new InvalidDataException("Identité du portefeuille non reconnue.");
     long cents=BitConverter.ToInt64(memory.Read(data+0x10,8),0);
     if(cents<0||cents>100000000000L)throw new InvalidDataException("Portefeuille non reconnu.");
     if(s.Wallet==0){s.Wallet=data+0x10;s.Cents=cents;s.WalletReference=reference;s.WalletItem=item;}
    }else{
     int count=BitConverter.ToInt32(memory.Read(data+0xc,4),0);uint id=Values.U(memory,data+4);
     if(count<0||count>100000||id==0||id>10000)throw new InvalidDataException("Munitions non reconnues.");
     if(slot==4)s.Reserves.Add(data+0xc);else if(id>1&&id<1000){uint typeTable=Values.Ptr(memory,data);uint typeMethod=Values.Ptr(memory,typeTable+4);byte[] method=memory.Read(typeMethod,6);if(method[0]==0x33&&method[1]==0xc0)s.Clips.Add(data+0xc);}
    }
   }
  }
  return s;
 }
 void ValidatePlayer(Snapshot s){uint game=Values.Ptr(memory,root);if(Values.Ptr(memory,game+0x118)!=s.Player||Values.Ptr(memory,s.Player+0xa0)!=s.Inventory)throw new InvalidDataException("Changement de scène.");}
 void ValidateWallet(Snapshot s){ValidatePlayer(s);if(s.Wallet==0)throw new InvalidOperationException("Portefeuille indisponible.");if(Values.Ptr(memory,s.WalletReference+8)!=s.Inventory||Values.Ptr(memory,s.WalletReference+0xc)!=s.WalletItem||Values.Ptr(memory,s.WalletItem+8)+0x10!=s.Wallet||Values.U(memory,s.Wallet-0xc)!=2000)throw new InvalidDataException("Le portefeuille a changé : écriture annulée.");}
 public void SetMoney(Snapshot s,long cents){if(cents<0||cents>100000000000L)throw new ArgumentOutOfRangeException("cents");ValidateWallet(s);memory.Write(s.Wallet,BitConverter.GetBytes(cents));}
 public void Tick(Snapshot s){
  ValidatePlayer(s);
  // Sécurité transitions scriptées (cinématiques, ascenseurs, chargements de zone) : pendant une perte de contrôle du joueur, le jeu détruit/reconstruit la scène. On retire TOUS les hooks et on n'écrit RIEN tant qu'on n'est pas en contrôle, + 2 s de marge après (la fermeture instantanée / R6025 survient en fin de transition). Tout reprend ensuite automatiquement. Signal validé en jeu le 22/10.
  if(!InControl(s))noControlUntil=DateTime.UtcNow.AddSeconds(2);
  if(DateTime.UtcNow<noControlUntil){if(hooks!=null)hooks.Dispose();return;}
  // Sécurité MG42 fixe : dès qu'elle (ID 14) est équipée, on arme une fenêtre de suspension qui couvre AUSSI les ~8 s suivant son rangement (sortie scriptée de l'appartement). Pendant cette fenêtre le trainer ne touche à RIEN (hooks retirés, aucune écriture), ce qui évite le crash au tir ET au moment de sortir, quels que soient les cheats combinés, et absorbe les lectures d'arme transitoires. Tout reprend ensuite automatiquement.
  if((God||Ammo||NoReload||Money||NoRecoil)&&CurrentWeaponId(s)==14)mg42Until=DateTime.UtcNow.AddSeconds(8);
  if(DateTime.UtcNow<mg42Until){if(hooks!=null)hooks.Dispose();return;}
  if(God){
   uint flag=s.Player+0xea;if(!godOriginal.ContainsKey(s.Player))godOriginal[s.Player]=memory.Read(flag,1)[0];
   if(memory.Read(flag,1)[0]!=1)memory.Write(flag,new byte[]{1});
   if(s.Health>0&&s.Health<s.MaxHealth)memory.Write(s.Player+0xd0,BitConverter.GetBytes(s.MaxHealth));
  }else RestoreGod(s.Player);
  if(Ammo){if(!ammoOriginal.ContainsKey(s.Inventory))ammoOriginal[s.Inventory]=memory.Read(s.Inventory+0xe8,1)[0];if(memory.Read(s.Inventory+0xe8,1)[0]!=1)memory.Write(s.Inventory+0xe8,new byte[]{1});foreach(uint a in s.Reserves)if(Values.U(memory,a)<999)memory.Write(a,BitConverter.GetBytes(999));}else RestoreAmmo(s.Inventory);
  if(hooks!=null){if(NoRecoil)hooks.ConfigureValidatedRecoilRows();hooks.Set(NoReload||Ammo,NoRecoil,s.Player,s.Inventory,NoRecoil);}
  if(Actions!=null)Actions.Poll();if(Body!=null)Body.Tick(s);
  bool actionBusy=(Body!=null&&Body.Busy);
  // Chargement forcé prioritaire sur la ré-application « Jamais recherché » : un seul canal Lua, pas de contention.
  if(Force!=null&&!actionBusy)Force.Tick(s);
  if(Protection!=null&&!actionBusy&&(Force==null||!Force.Active))Protection.Tick(s);
  // Chargeur illimité / munitions : on ne réécrit PLUS le compteur de chargeur depuis l'extérieur (course avec le jeu = crash en fusillade scriptée, isolé le 17 oct). Le maintien du chargeur est assuré uniquement par le hook interne 0x5A46E0, exécuté dans le fil du jeu au bon moment.
  if(Money&&s.Wallet!=0&&s.Cents<99999900)SetMoney(s,99999900L);
 }
 public void AddMoney(Snapshot s){SetMoney(s,Math.Min(s.Cents+1000000,100000000000L));}
 void RestoreGod(uint player){byte b;if(godOriginal.TryGetValue(player,out b)){memory.Write(player+0xea,new byte[]{b});godOriginal.Remove(player);}}
 void RestoreAmmo(uint inventory){byte b;if(ammoOriginal.TryGetValue(inventory,out b)){memory.Write(inventory+0xe8,new byte[]{b});ammoOriginal.Remove(inventory);}}
 public void Suspend(){if(hooks!=null)hooks.Set(false,false);try{uint player=Values.Ptr(memory,Values.Ptr(memory,root)+0x118);uint script=Values.Ptr(memory,player+0xa4);if(Values.Ptr(memory,script)==player){RestoreGod(player);RestoreAmmo(Values.Ptr(memory,player+0xa0));}}catch{}}
 public void Stop(){if(Force!=null)Force.Cancel();if(Protection!=null)Protection.Request(false);if(Actions!=null&&(Protection==null||!Protection.Pending)&&(Body==null||!Body.Busy))Actions.CancelPending();God=Ammo=NoReload=Money=NoRecoil=false;Suspend();if(hooks!=null)hooks.Uninstall();godOriginal.Clear();ammoOriginal.Clear();}
 public void Close(){Stop();if(Actions!=null)Actions.Dispose();if(hooks!=null){hooks.Dispose();if(hooks.RestoreError!="")try{File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"trainer.log"),"Restauration partielle : "+hooks.RestoreError+Environment.NewLine);}catch{}}}
}
sealed class GameSession:IDisposable {
 public const string SupportedHash="E389B275C40C3623AC0F646E9F42FF875011254F2A92100A18DB545FB66E112F";
 Process process;NativeMemory memory;public Cheats Engine;public string Description="Jeu fermé";public string CarsDirectory="";
 public long ReadCalls{get{return memory==null?0:memory.ReadCalls;}}public long WriteCalls{get{return memory==null?0:memory.WriteCalls;}}
 public bool Connected{get{return process!=null&&!process.HasExited;}}
 public void Connect(){
  Dispose();Process[] found=Process.GetProcessesByName("mafia2");if(found.Length!=1)throw new InvalidOperationException("Lance Mafia II Classic et charge une partie solo.");
  Process candidate=found[0];string file=candidate.MainModule.FileName;
  try{CarsDirectory=Path.Combine(Path.GetDirectoryName(file),"sds","cars");}catch{CarsDirectory="";}
  using(var sha=SHA256.Create())using(var stream=File.OpenRead(file)){
   string hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
   if(hash!=SupportedHash&&hash!="88F60CD24C8D2FB874526B0559E55EF447C3B3A288DBFC4D2B9A4BB0770023C6")throw new InvalidOperationException("Exécutable différent de la version vérifiée. Connexion refusée.");
  }
  foreach(ProcessModule module in candidate.Modules){string name=module.ModuleName.ToLowerInvariant();if(name.Contains("m2o")||name.Contains("m2mp")||name.Contains("mafia2online"))throw new InvalidOperationException("Module multijoueur détecté. Trainer réservé au solo.");}
  uint root=checked((uint)candidate.MainModule.BaseAddress.ToInt64()+0x16bfe14);
  memory=new NativeMemory(candidate.Id);process=candidate;Engine=new Cheats(memory,root,(uint)candidate.MainModule.BaseAddress.ToInt64());Description="Mafia II Classic · PID "+candidate.Id;
 }
 public void Dispose(){if(Engine!=null){if(Connected)Engine.Close();Engine=null;}if(memory!=null){memory.Dispose();memory=null;}if(process!=null){process.Dispose();process=null;}}
}
static class Backups {
 public static string SaveFolder{get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"2K Games\Mafia II\Saves");}}
 public static string Create(){
  if(!Directory.Exists(SaveFolder))throw new DirectoryNotFoundException("Sauvegardes Mafia II introuvables.");
  string dest=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Backups",DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff"));
  foreach(string file in Directory.GetFiles(SaveFolder,"*",SearchOption.AllDirectories)){
   string target=Path.Combine(dest,file.Substring(SaveFolder.Length+1));Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(file,target,false);
  }return dest;
 }
}
sealed class Toggle:Control {
 public bool On;public event EventHandler Changed;
 public Toggle(){Size=new Size(64,32);Cursor=Cursors.Hand;SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true);TabStop=true;AccessibleRole=AccessibleRole.CheckButton;}
 protected override void OnClick(EventArgs e){if(!Enabled)return;On=!On;Invalidate();if(Changed!=null)Changed(this,EventArgs.Empty);base.OnClick(e);}
 protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space){OnClick(EventArgs.Empty);e.Handled=true;}base.OnKeyDown(e);}
 protected override void OnPaint(PaintEventArgs e){
  var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
  using(var bg=new SolidBrush(Parent!=null?Parent.BackColor:Color.FromArgb(22,27,36)))g.FillRectangle(bg,ClientRectangle);
  var track=new Rectangle(1,3,60,26);
  Color onA=Color.FromArgb(221,183,117),onB=Color.FromArgb(188,150,88),off=Color.FromArgb(49,56,70);
  using(var path=Round(track,13)){
   if(On){using(var lg=new System.Drawing.Drawing2D.LinearGradientBrush(track,onA,onB,90f))g.FillPath(lg,path);}
   else using(var b=new SolidBrush(off))g.FillPath(b,path);
   using(var pen=new Pen(On?Color.FromArgb(70,0,0,0):Color.FromArgb(64,72,88),1))g.DrawPath(pen,path);
  }
  int kx=On?35:4;var knob=new Rectangle(kx,6,20,20);
  using(var sh=new SolidBrush(Color.FromArgb(55,0,0,0)))g.FillEllipse(sh,kx,8,20,20);
  using(var b=new SolidBrush(Enabled?Color.White:Color.FromArgb(150,157,170)))g.FillEllipse(b,knob);
 }
 public static GraphicsPath Round(Rectangle r,int radius){var p=new GraphicsPath();int d=Math.Max(1,radius*2);if(d>r.Width)d=r.Width;if(d>r.Height)d=r.Height;p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
}
sealed class NavigationTab:Control {
 public bool Selected;bool hovered;
 public NavigationTab(){TabStop=true;Cursor=Cursors.Hand;AccessibleRole=AccessibleRole.PageTab;Font=new Font("Segoe UI",10,FontStyle.Bold);SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
 protected override void OnMouseEnter(EventArgs e){hovered=true;Invalidate();base.OnMouseEnter(e);}
 protected override void OnMouseLeave(EventArgs e){hovered=false;Invalidate();base.OnMouseLeave(e);}
 protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
 protected override void OnLostFocus(EventArgs e){Invalidate();base.OnLostFocus(e);}
 protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter){OnClick(EventArgs.Empty);e.Handled=true;}base.OnKeyDown(e);}
 protected override void OnPaint(PaintEventArgs e){
  var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;Color gold=Color.FromArgb(214,180,120);
  using(var bg=new SolidBrush(Parent!=null?Parent.BackColor:Color.FromArgb(13,16,22)))g.FillRectangle(bg,ClientRectangle);
  using(var path=Toggle.Round(new Rectangle(1,1,Width-3,Height-3),11)){
   using(var brush=new SolidBrush(Selected?Color.FromArgb(49,41,27):hovered?Color.FromArgb(32,39,51):Color.FromArgb(21,26,35)))g.FillPath(brush,path);
   using(var pen=new Pen(Selected?gold:Focused?Color.FromArgb(120,214,180,120):Color.FromArgb(44,51,65),Selected?1.4f:1f))g.DrawPath(pen,path);
  }
  TextRenderer.DrawText(g,Text,Font,ClientRectangle,Selected?gold:hovered?Color.FromArgb(206,214,228):Color.FromArgb(150,160,178),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
 }
}
sealed class DarkTabs:Panel {
 readonly System.Collections.Generic.List<Panel> pages=new System.Collections.Generic.List<Panel>();readonly System.Collections.Generic.List<NavigationTab> headers=new System.Collections.Generic.List<NavigationTab>();int selected;
 [System.ComponentModel.DefaultValue(0)] public int SelectedIndex {get=>selected;set{if(pages.Count==0)return;selected=Math.Max(0,Math.Min(value,pages.Count-1));for(int i=0;i<pages.Count;i++){pages[i].Visible=i==selected;headers[i].Selected=i==selected;headers[i].Invalidate();}pages[selected].BringToFront();}}
 public DarkTabs(){BackColor=Color.FromArgb(13,16,22);SetStyle(ControlStyles.OptimizedDoubleBuffer,true);}
 public Panel AddPage(string title){
  int index=pages.Count;var page=new Panel{BackColor=BackColor,Bounds=new Rectangle(0,50,Width,Height-50),AccessibleName=title};var header=new NavigationTab{Text=title,AccessibleName=title,Bounds=new Rectangle(index*236,0,224,42)};
  header.Click+=(s,e)=>SelectedIndex=index;header.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Right||e.KeyCode==Keys.Left){SelectedIndex=(selected+(e.KeyCode==Keys.Right?1:pages.Count-1))%pages.Count;headers[selected].Focus();e.Handled=true;}};
  pages.Add(page);headers.Add(header);Controls.Add(page);Controls.Add(header);int headerWidth=(Width-12*(headers.Count-1))/headers.Count;for(int i=0;i<headers.Count;i++)headers[i].Bounds=new Rectangle(i*(headerWidth+12),0,headerWidth,42);SelectedIndex=selected;return page;
 }
}
sealed class ReadableButton:Button {
 bool hover,down;public int Radius=10;public Color Accent=Color.Empty;
 public ReadableButton(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;}
 protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
 protected override void OnMouseLeave(EventArgs e){hover=false;down=false;Invalidate();base.OnMouseLeave(e);}
 protected override void OnMouseDown(MouseEventArgs e){down=true;Invalidate();base.OnMouseDown(e);}
 protected override void OnMouseUp(MouseEventArgs e){down=false;Invalidate();base.OnMouseUp(e);}
 static Color Shift(Color c,int d){return Color.FromArgb(Math.Max(0,Math.Min(255,c.R+d)),Math.Max(0,Math.Min(255,c.G+d)),Math.Max(0,Math.Min(255,c.B+d)));}
 protected override void OnPaint(PaintEventArgs e){
  var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
  using(var pb=new SolidBrush(Parent!=null?Parent.BackColor:Color.FromArgb(13,16,22)))g.FillRectangle(pb,ClientRectangle);
  Color baseColor=Enabled?(down?Shift(BackColor,-10):hover?Shift(BackColor,14):BackColor):Color.FromArgb(26,31,40);
  var r=new Rectangle(0,0,Width-1,Height-1);
  using(var path=Toggle.Round(r,Radius)){
   using(var b=new SolidBrush(baseColor))g.FillPath(b,path);
   using(var pen=new Pen(Enabled?(Accent!=Color.Empty?Accent:Shift(BackColor,24)):Color.FromArgb(44,51,65),1))g.DrawPath(pen,path);
  }
  Color fore=Enabled?ForeColor:Color.FromArgb(120,128,142);
  TextRenderer.DrawText(g,Text,Font,ClientRectangle,fore,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak|TextFormatFlags.EndEllipsis);
 }
}
// Panneau arrondi avec bordure et liseré d'accent optionnel.
sealed class Card:Panel {
 public int Radius=14;public Color Border=Color.FromArgb(40,47,61);public Color Accent=Color.Empty;
 public Card(){DoubleBuffered=true;SetStyle(ControlStyles.ResizeRedraw,true);}
 protected override void OnResize(EventArgs e){base.OnResize(e);if(Width>0&&Height>0)using(var p=Toggle.Round(new Rectangle(0,0,Width,Height),Radius))Region=new Region(p);}
 protected override void OnPaint(PaintEventArgs e){
  base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
  using(var p=Toggle.Round(new Rectangle(0,0,Width-1,Height-1),Radius))using(var pen=new Pen(Border,1))g.DrawPath(pen,p);
  if(Accent!=Color.Empty)using(var b=new SolidBrush(Accent))g.FillRectangle(b,0,0,4,Height);
 }
}
sealed class TrainerForm:Form {
 readonly Color bg=Color.FromArgb(13,16,22),card=Color.FromArgb(22,27,36),cardAlt=Color.FromArgb(28,34,45),gold=Color.FromArgb(214,180,120),muted=Color.FromArgb(139,149,167),stroke=Color.FromArgb(40,47,61),emerald=Color.FromArgb(96,189,128),danger=Color.FromArgb(206,88,94);
 readonly GameSession session=new GameSession();readonly Timer timer=new Timer();readonly Toggle[] toggles=new Toggle[5];
 readonly object gate=new object();System.Threading.Timer worker;string workerError="";volatile bool closing;int workerTicks;readonly Button addMoney;
 Label status,details,health,wallet,notice;Snapshot snapshot;readonly bool preview;bool closeWhenSafe;
 Button repairVehicle,refuel,give,readCatalogue,readCars,spawnCar;Toggle policeToggle;ComboBox cars,weapons;NumericUpDown giftAmmo;Label actionStatus;NavigationTab availableWeapons,unavailableWeapons;bool showUnavailableWeapons;readonly List<WeaponEntry> weaponCatalogue=new List<WeaponEntry>();
 bool[] repairRestore;bool repairPending;
 [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h,int id,uint mod,uint key);
 [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h,int id);
 [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
 public TrainerForm(bool render){
  preview=render;Text="Empire Bay · Mafia II Solo Trainer";ClientSize=new Size(980,844);MinimumSize=new Size(996,883);StartPosition=FormStartPosition.CenterScreen;BackColor=bg;ForeColor=Color.White;Font=new Font("Segoe UI",10);AutoScaleMode=AutoScaleMode.None;
  Load+=(s,e)=>{if(!preview)BeginInvoke(new Action(()=>ShowWindow(Handle,1)));};
  var rail=new Panel{BackColor=Color.FromArgb(17,21,29),Bounds=new Rectangle(0,0,220,844)};Controls.Add(rail);
  Controls.Add(new Panel{BackColor=stroke,Bounds=new Rectangle(220,0,1,844)});
  var badge=new Card{BackColor=Color.FromArgb(39,33,22),Border=Color.FromArgb(120,214,180,120),Radius=12,Bounds=new Rectangle(25,26,54,54)};rail.Controls.Add(badge);
  AddLabel(badge,"EB",0,10,54,32,20,gold,FontStyle.Bold).TextAlign=ContentAlignment.MiddleCenter;
  AddLabel(rail,"EMPIRE BAY",90,31,126,24,12,Color.White,FontStyle.Bold);
  AddLabel(rail,"SOLO TRAINER",91,56,126,20,8,muted,FontStyle.Regular);
  rail.Controls.Add(new Panel{BackColor=stroke,Bounds=new Rectangle(25,102,170,1)});
  AddLabel(rail,"MAFIA II",26,210,180,35,23,Color.White,FontStyle.Bold);AddLabel(rail,"CLASSIC · STEAM",27,249,175,24,9,gold,FontStyle.Regular);
  var top=new CheckBox{Text="Garder le trainer visible",Bounds=new Rectangle(25,330,185,45),ForeColor=muted};top.CheckedChanged+=(s,e)=>TopMost=top.Checked;rail.Controls.Add(top);
  var tips=new ToolTip();tips.SetToolTip(top,"Garde la fenêtre du trainer au-dessus des autres. Sans effet sur les cheats en arrière-plan.");
  var patchBtn=ButtonAt(rail,"Patch 4 Go du jeu",25,400,170,42);patchBtn.Click+=(s,e)=>PatchFlow();tips.SetToolTip(patchBtn,"Vérifie et installe le patch 4 Go (LargeAddressAware) sur mafia2.exe. Jeu fermé requis pour l'installation.");
  AddLabel(rail,"Évite des crashes mémoire du jeu 32 bits.",25,444,180,34,8,muted,FontStyle.Regular);
  AddLabel(rail,"Raccourci clavier : CTRL + ALT + F12",25,568,180,40,9,muted,FontStyle.Regular);
  var disableAll=ButtonAt(rail,"Tout désactiver",25,672,170,50);disableAll.BackColor=Color.FromArgb(126,42,48);disableAll.Font=new Font("Segoe UI",12,FontStyle.Bold);disableAll.Click+=(s,e)=>Stop();
  tips.SetToolTip(disableAll,"Coupe tous les cheats et retire les modifications du jeu. À utiliser avant un passage sensible (ex. sortie scriptée).");
  AddLabel(this,"Ton Empire Bay. Tes règles.",252,28,700,44,23,Color.White,FontStyle.Bold);
  AddLabel(this,"Mafia II Classic  /  Partie solo",254,76,680,24,10,muted,FontStyle.Regular);
  var conn=new Card{BackColor=card,Border=stroke,Accent=gold,Bounds=new Rectangle(252,120,696,94)};Controls.Add(conn);
  status=AddLabel(conn,"●  EN ATTENTE DU JEU",20,15,470,27,12,gold,FontStyle.Bold);
  details=AddLabel(conn,"Lance le jeu, puis charge une partie.",20,48,470,24,9,muted,FontStyle.Regular);
  var connect=ButtonAt(conn,"Connecter",532,25,142,43);connect.Click+=(s,e)=>Connect();
  var healthChip=new Card{BackColor=card,Border=stroke,Radius=12,Bounds=new Rectangle(252,220,338,42)};Controls.Add(healthChip);
  health=AddLabel(healthChip,"SANTÉ  —",18,0,300,42,11,muted,FontStyle.Bold);health.TextAlign=ContentAlignment.MiddleLeft;
  var walletChip=new Card{BackColor=card,Border=stroke,Radius=12,Bounds=new Rectangle(610,220,338,42)};Controls.Add(walletChip);
  wallet=AddLabel(walletChip,"ARGENT  —",18,0,300,42,11,muted,FontStyle.Bold);wallet.TextAlign=ContentAlignment.MiddleLeft;
  string[] titles={"Invincibilité","Munitions infinies","Chargeur illimité","Argent illimité","Sans recul + précision"};
  string[] subtitles={"Protection et santé maintenue au maximum.","Réserve illimitée et chargeur qui ne diminue plus.","Empêche la dépense des balles dans le chargeur.","Maintient le portefeuille à au moins 999 999 $.","Toutes les armes de Vito. Protection auto sur la MG42 fixe."};
  for(int i=0;i<5;i++){
   int index=i;var panel=new Card{BackColor=card,Border=stroke,Radius=12,Bounds=new Rectangle(252,273+i*83,696,72)};Controls.Add(panel);
   AddLabel(panel,titles[i],19,9,470,27,12,Color.White,FontStyle.Bold);AddLabel(panel,subtitles[i],20,40,500,22,9,muted,FontStyle.Regular);
   AddLabel(panel,"F"+(i+1),548,22,43,27,10,gold,FontStyle.Bold);
   var toggle=new Toggle{Location=new Point(610,19),Enabled=false,AccessibleName=titles[i]};panel.Controls.Add(toggle);toggles[i]=toggle;
   toggle.Changed+=(s,e)=>ApplyToggle(index);
  }
  addMoney=ButtonAt(this,"+ 10 000 $   ·   Ctrl + Alt + F6",252,700,325,42);addMoney.Enabled=false;addMoney.Click+=(s,e)=>Run(()=>{if(session.Engine==null||snapshot==null)return;lock(gate)session.Engine.AddMoney(session.Engine.Inspect());notice.Text="10 000 $ ajoutés au portefeuille.";});
  var stop=ButtonAt(this,"Tout désactiver",599,700,349,42);stop.Click+=(s,e)=>Stop();
  notice=AddLabel(this,"Connexion nécessaire · Aucun cheat actif",254,756,694,46,9,muted,FontStyle.Regular);
  AddLabel(this,"Version 0.4 · Trainer séparé pour Mafia II Classic solo",254,812,694,22,8,muted,FontStyle.Regular);
  timer.Interval=200;timer.Tick+=(s,e)=>RefreshGame();FormClosing+=(s,e)=>{lock(gate){if(session.Connected&&(session.Engine?.Protection?.NeedsCleanup==true||session.Engine?.Body?.Busy==true)){session.Engine.Protection.Request(false);closeWhenSafe=true;e.Cancel=true;notice.Text="Reprends le jeu sans pause pour terminer les actions avant fermeture.";return;}}closing=true;timer.Stop();if(worker!=null)worker.Dispose();lock(gate)session.Dispose();};
  BuildExperimentalUi();
  if(!preview){worker=new System.Threading.Timer(_=>WorkerTick(),null,100,100);timer.Start();}
 }
 Label AddLabel(Control parent,string text,int x,int y,int w,int h,float size,Color color,FontStyle style){var l=new Label{Text=text,Bounds=new Rectangle(x,y,w,h),Font=new Font("Segoe UI",size,style),ForeColor=color,BackColor=Color.Transparent};parent.Controls.Add(l);return l;}
 void BuildExperimentalUi(){
  var tabs=new DarkTabs{Bounds=new Rectangle(252,265,696,480)};
  var main=tabs.AddPage("Cheats");var vehicles=tabs.AddPage("Police & véhicule");var arms=tabs.AddPage("Armes");var spawn=tabs.AddPage("Spawner");
  foreach(Control c in Controls.Cast<Control>().Where(c=>c.Left>=252&&c.Top>=273&&c.Top<745).ToArray()){
   Controls.Remove(c);int oldY=c.Top;c.Left-=252;
   if(c is Panel){c.Top=(oldY-273)/83*76;c.Height=65;c.Width=680;}else{c.Top=388;if(c.Right>680)c.Width=680-c.Left;}
   main.Controls.Add(c);
  }Controls.Add(tabs);
  AddLabel(vehicles,"Protection policière et véhicule",20,18,640,36,14,Color.White,FontStyle.Bold);
  AddLabel(vehicles,"Prévention sur Vito : empêche d'être recherché (arme, crime…).\nN'efface pas une recherche déjà en cours. Reprends le jeu pour appliquer.",20,60,640,48,10,muted,FontStyle.Regular);
  // « Jamais recherché » en interrupteur, comme les autres cheats.
  var policeCard=new Card{BackColor=card,Border=stroke,Radius=12,Bounds=new Rectangle(20,120,640,66)};vehicles.Controls.Add(policeCard);
  AddLabel(policeCard,"Jamais recherché",18,9,470,26,12,Color.White,FontStyle.Bold);AddLabel(policeCard,"Aucune recherche policière possible sur Vito.",19,38,500,20,9,muted,FontStyle.Regular);
  policeToggle=new Toggle{Location=new Point(560,17),Enabled=false,AccessibleName="Jamais recherché"};policeCard.Controls.Add(policeToggle);
  policeToggle.Changed+=(s,e)=>Run(()=>{lock(gate){if(session.Engine==null){policeToggle.On=false;return;}if(session.Engine.Body.Busy){policeToggle.On=false;throw new InvalidOperationException("Attends la fin de la réparation du véhicule.");}var protection=session.Engine.Protection;protection.Request(policeToggle.On);protection.Tick(session.Engine.Inspect());notice.Text=protection.Status;}});
  repairVehicle=ButtonAt(vehicles,"Réparer le véhicule · moteur + carrosserie",20,200,640,48);refuel=ButtonAt(vehicles,"Remplir le réservoir",20,262,640,48);
  refuel.Click+=(s,e)=>Run(()=>{lock(gate){if(session.Engine==null)throw new InvalidOperationException("Connecte le trainer.");if(session.Engine.Body.Busy)throw new InvalidOperationException("Attends la fin de la réparation du véhicule.");var fuel=session.Engine.VehicleFuel;var result=fuel.Fill(fuel.InspectFuel());notice.Text="Réservoir : "+result.Current.ToString("0.0")+" / "+result.Capacity.ToString("0.0")+" L. Plein effectué.";Log(notice.Text);}});
  // Bouton combiné : répare moteur + carrosserie en une action. Coupe automatiquement les cheats actifs (contrainte carrosserie) et les réactive à la fin de la réparation.
  repairVehicle.Click+=(s,e)=>Run(()=>{lock(gate){var engine=session.Engine;if(engine==null)throw new InvalidOperationException("Connecte le trainer.");if(engine.Body.Busy)throw new InvalidOperationException("Réparation déjà en cours.");if(engine.Protection.NeedsCleanup)throw new InvalidOperationException("Désactive « Jamais recherché » avant de réparer.");var current=engine.Inspect();if(current.Car==0||current.Health<=0)throw new InvalidOperationException("Monte dans le véhicule, à l'arrêt.");repairRestore=new bool[]{engine.God,engine.Ammo,engine.NoReload,engine.Money,engine.NoRecoil};repairPending=repairRestore.Any(b=>b);engine.God=engine.Ammo=engine.NoReload=engine.Money=engine.NoRecoil=false;foreach(var t in toggles){t.On=false;t.Invalidate();}engine.Suspend();engine.Body.Repair(current);notice.Text=repairPending?"Réparation lancée · cheats réactivés à la fin. Reste immobile.":engine.Body.Status;}});
  actionStatus=AddLabel(vehicles,"Véhicule : au volant et à l'arrêt. Les cheats actifs se coupent le temps de la réparation puis se réactivent.",20,324,640,80,9,gold,FontStyle.Regular);
  AddLabel(arms,"Donner une arme",20,18,630,36,15,Color.White,FontStyle.Bold);
  AddLabel(arms,"Toutes les armes utilisables du catalogue chargé sont donnables.\nExclues : MG42 fixe (non portable) et grenade allemande (inutilisable).",20,62,640,48,10,muted,FontStyle.Regular);
  availableWeapons=new NavigationTab{Text="Disponibles à l’ajout",Bounds=new Rectangle(20,117,310,42)};unavailableWeapons=new NavigationTab{Text="Non disponibles",Bounds=new Rectangle(350,117,310,42)};arms.Controls.Add(availableWeapons);arms.Controls.Add(unavailableWeapons);availableWeapons.Click+=(s,e)=>ShowWeapons(false);unavailableWeapons.Click+=(s,e)=>ShowWeapons(true);
  weapons=new ComboBox{Bounds=new Rectangle(20,174,640,35),DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=28,BackColor=card,ForeColor=Color.White};weapons.DrawItem+=ComboDraw;arms.Controls.Add(weapons);foreach(var w in ActionScripts.KnownWeapons)weaponCatalogue.Add(new WeaponEntry{Id=w.Key,Name=w.Value});
  readCatalogue=ButtonAt(arms,"Actualiser les armes depuis le jeu",20,224,640,40);readCatalogue.Click+=(s,e)=>Run(()=>{lock(gate){var items=session.Engine.Actions.Catalogue();weaponCatalogue.Clear();weaponCatalogue.AddRange(items);ShowWeapons(showUnavailableWeapons);}});
  AddLabel(arms,"Munitions à ajouter",20,282,260,28,11,muted,FontStyle.Regular);giftAmmo=new NumericUpDown{Bounds=new Rectangle(325,279,160,32),Minimum=1,Maximum=300,Value=120,BorderStyle=BorderStyle.FixedSingle,BackColor=card,ForeColor=Color.White};arms.Controls.Add(giftAmmo);
  give=ButtonAt(arms,"Ajouter l'arme sélectionnée",20,333,640,44);give.Click+=(s,e)=>{var item=weapons.SelectedItem as WeaponEntry;if(item==null||!item.GiveValidated){notice.Text="Clique d'abord « Actualiser les armes », puis choisis une arme portable (la MG42 n'est pas donnable).";return;}QueueAction(ActionScripts.GiveWeapon(item.Id,ActionScripts.CapAmmo(item.Id,(int)giftAmmo.Value)),false);};
  weapons.SelectedIndexChanged+=(s,e)=>{var it=weapons.SelectedItem as WeaponEntry;int max=(it!=null&&ActionScripts.IsProjectile(it.Id))?ActionScripts.ProjectileMax:ActionScripts.FirearmMax;giftAmmo.Maximum=max;if(giftAmmo.Value>max)giftAmmo.Value=max;RefreshGame();}; AddLabel(arms,"Les armes non disponibles restent consultables, sans ajout possible.\nAucune arme n’est donnée automatiquement.",20,390,640,30,9,muted,FontStyle.Regular);
  AddLabel(arms,"⚠  Préfère les armes déjà présentes dans ton chapitre.",20,426,640,22,10,danger,FontStyle.Bold);
  AddLabel(arms,"Une arme absente de ton chapitre/DLC actuel (ex. une arme de la campagne dans Joe's Adventures) peut apparaître sans modèle en main, ou faire planter le jeu dès qu'une fusillade / recherche police commence : ses ressources ne sont pas chargées dans ce contexte.",20,450,640,60,9,muted,FontStyle.Regular);
  AddLabel(spawn,"Faire apparaître un véhicule",20,18,640,35,14,Color.White,FontStyle.Bold);
  AddLabel(spawn,"À pied, emplacement dégagé, hors cinématique. Voiture ~8 m devant Vito.",20,58,640,22,10,muted,FontStyle.Regular);
  cars=new ComboBox{Bounds=new Rectangle(20,90,640,35),DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=28,BackColor=card,ForeColor=Color.White};cars.DrawItem+=ComboDraw;spawn.Controls.Add(cars);
  readCars=ButtonAt(spawn,"Lire les véhicules disponibles dans la zone",20,134,640,40);readCars.Click+=(s,e)=>Run(()=>{lock(gate){
   List<VehicleEntry> loaded;try{loaded=session.Engine.Actions.Vehicles();}catch{loaded=new List<VehicleEntry>();}
   var choices=loaded.Select(v=>new CarChoice{Name=v.Name,Loaded=v}).OrderBy(c=>c.Name,StringComparer.OrdinalIgnoreCase).ToList();
   cars.Items.Clear();cars.Items.AddRange(choices.Cast<object>().ToArray());if(cars.Items.Count>0)cars.SelectedIndex=0;
   notice.Text=choices.Count+" véhicule(s) chargé(s) dans ce chapitre, prêts à apparaître.";
  }});
  spawnCar=ButtonAt(spawn,"Faire apparaître la voiture sélectionnée",20,180,640,44);spawnCar.Click+=(s,e)=>Run(()=>{lock(gate){if(session.Engine==null)throw new InvalidOperationException("Connecte le trainer à une partie solo chargée.");if(session.Engine.Body.Busy)throw new InvalidOperationException("Attends la fin de la réparation carrosserie.");if(session.Engine.Protection.Pending)throw new InvalidOperationException("Attends la fin du changement de protection police.");var choice=cars.SelectedItem as CarChoice;if(choice==null)throw new InvalidOperationException("Lis la liste puis choisis un véhicule.");if(!choice.IsLoaded)throw new InvalidOperationException("« "+choice.Name.Replace('_',' ')+" » n'est pas chargée dans ce chapitre : apparition impossible. Choisis une voiture marquée « chargé — apparition immédiate ».");session.Engine.Actions.Spawn(session.Engine.Inspect(),choice.Loaded);notice.Text=session.Engine.Actions.LastStatus;}});
  AddLabel(spawn,"Liste des véhicules présents dans ton chapitre, prêts à apparaître. Garde une sauvegarde. 3 apparitions max par session.",20,228,640,40,9,muted,FontStyle.Regular);
  AddLabel(spawn,"⚠  Voitures pour le free-roam uniquement.",20,288,640,22,10,danger,FontStyle.Bold);
  AddLabel(spawn,"Ne pas les amener en customisation (Bomba), au garage ou dans une mission scénarisée : la voiture créée n'est pas un véhicule complet du jeu et le passage par une cinématique peut faire planter le jeu (R6025).",20,312,640,56,9,muted,FontStyle.Regular);
  cars.SelectedIndexChanged+=(s,e)=>RefreshGame();readCars.Enabled=spawnCar.Enabled=false;
  policeToggle.Enabled=false;repairVehicle.Enabled=refuel.Enabled=give.Enabled=readCatalogue.Enabled=false;ShowWeapons(false);
 }
 void ShowWeapons(bool unavailable){
  showUnavailableWeapons=unavailable;int selectedId=(weapons.SelectedItem as WeaponEntry)?.Id??-1;
  availableWeapons.Selected=!unavailable;unavailableWeapons.Selected=unavailable;
  availableWeapons.Text="Disponibles à l’ajout ("+weaponCatalogue.Count(w=>w.GiveTested)+")";unavailableWeapons.Text="Non disponibles ("+weaponCatalogue.Count(w=>!w.GiveTested)+")";availableWeapons.Invalidate();unavailableWeapons.Invalidate();
  var items=weaponCatalogue.Where(w=>w.GiveTested!=unavailable).OrderBy(w=>w.Name).ToArray();weapons.Items.Clear();weapons.Items.AddRange(items.Cast<object>().ToArray());if(items.Length>0){int index=Array.FindIndex(items,w=>w.Id==selectedId);weapons.SelectedIndex=index<0?0:index;}RefreshGame();
 }
 void QueueAction(string source,bool vehicle,bool removeCallback=false){Run(()=>{lock(gate){if(session.Engine==null||session.Engine.Actions==null)throw new InvalidOperationException("Connecte le trainer à une partie solo chargée.");if(session.Engine.Body.Busy)throw new InvalidOperationException("Attends la fin de la réparation carrosserie.");if(session.Engine.Protection.Pending)throw new InvalidOperationException("Attends la fin du changement de protection police.");session.Engine.Actions.Queue(session.Engine.Inspect(),source,vehicle,removeCallback);notice.Text=session.Engine.Actions.LastStatus;}});}
 Button ButtonAt(Control parent,string text,int x,int y,int w,int h){var b=new ReadableButton{Text=text,Bounds=new Rectangle(x,y,w,h),BackColor=Color.FromArgb(37,44,57),ForeColor=Color.White,FlatStyle=FlatStyle.Flat,Cursor=Cursors.Hand};b.FlatAppearance.BorderSize=0;parent.Controls.Add(b);return b;}
 void ComboDraw(object sender,DrawItemEventArgs e){var c=(ComboBox)sender;bool sel=(e.State&DrawItemState.Selected)!=0;using(var b=new SolidBrush(sel?Color.FromArgb(49,41,27):card))e.Graphics.FillRectangle(b,e.Bounds);string text=e.Index>=0?c.Items[e.Index].ToString():c.Text;TextRenderer.DrawText(e.Graphics,text,c.Font,e.Bounds,sel?gold:Color.White,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);}
 protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);if(!preview){for(int i=1;i<=6;i++)if(!RegisterHotKey(Handle,i,0x4003,(uint)(Keys.F1+i-1))&&notice!=null)notice.Text="Un raccourci est déjà utilisé. Les boutons restent disponibles.";RegisterHotKey(Handle,12,0x4003,(uint)Keys.F12);}}
 protected override void OnHandleDestroyed(EventArgs e){for(int i=1;i<=6;i++)UnregisterHotKey(Handle,i);UnregisterHotKey(Handle,12);base.OnHandleDestroyed(e);}
 protected override void WndProc(ref Message m){if(m.Msg==0x312){int id=m.WParam.ToInt32();if(id>=1&&id<=5&&toggles[id-1].Enabled){toggles[id-1].On=!toggles[id-1].On;toggles[id-1].Invalidate();ApplyToggle(id-1);}if(id==6&&addMoney.Enabled)addMoney.PerformClick();if(id==12)Stop();}base.WndProc(ref m);}
 void Run(Action action){try{action();}catch(Exception ex){notice.Text=ex.Message;Log(ex.Message);}}
 // Bouton « Patch 4 Go » : vérifie l'état du drapeau LargeAddressAware de mafia2.exe et, s'il manque, propose
 // de l'installer (jeu fermé obligatoire). Déjà présent → message. Sauvegarde de l'exe original avant modification.
 void PatchFlow(){
  try{
   string path=GamePatch.LocateExe();
   if(path==""){
    using(var dlg=new OpenFileDialog{Title="Où est mafia2.exe ?",Filter="mafia2.exe|mafia2.exe|Exécutable (*.exe)|*.exe",CheckFileExists=true}){
     if(dlg.ShowDialog(this)!=DialogResult.OK){notice.Text="Localisation de mafia2.exe annulée.";return;}
     path=dlg.FileName;
    }
   }
   var state=GamePatch.Inspect(path);
   if(state==PatchState.Patched){MessageBox.Show(this,"Le patch 4 Go (LargeAddressAware) est déjà installé sur :\n"+path+"\n\nSi le jeu plante encore, une explication détaillée sera ajoutée prochainement dans le trainer.","Patch 4 Go — déjà installé",MessageBoxButtons.OK,MessageBoxIcon.Information);notice.Text="Patch 4 Go déjà installé.";return;}
   if(state==PatchState.ExeNotFound){MessageBox.Show(this,"mafia2.exe est introuvable à l'emplacement indiqué.","Patch 4 Go",MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
   if(state==PatchState.NotPe32){MessageBox.Show(this,"Ce fichier n'est pas un mafia2.exe 32 bits reconnu :\n"+path,"Patch 4 Go",MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
   if(GamePatch.GameRunning()){MessageBox.Show(this,"Mafia II est en cours d'exécution.\nFerme complètement le jeu, puis relance l'installation du patch.","Patch 4 Go — jeu ouvert",MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
   if(MessageBox.Show(this,"Le patch 4 Go (LargeAddressAware) n'est pas installé.\n\nIl autorise le jeu 32 bits à utiliser jusqu'à ~4 Go de mémoire et évite des crashes.\nUne sauvegarde de l'exe original sera créée avant toute modification.\n\nInstaller maintenant ?","Patch 4 Go — installation",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes){notice.Text="Installation du patch annulée.";return;}
   string backup=GamePatch.Apply(path);
   Log("Patch 4 Go appliqué sur "+path+" (sauvegarde "+backup+")");
   MessageBox.Show(this,"Patch 4 Go installé avec succès.\n\nSauvegarde de l'exe original :\n"+backup+"\n\nTu peux lancer le jeu.","Patch 4 Go — terminé",MessageBoxButtons.OK,MessageBoxIcon.Information);
   notice.Text="Patch 4 Go installé.";
  }catch(Exception ex){Log("Patch 4 Go — échec : "+ex.Message);MessageBox.Show(this,"Installation impossible :\n"+ex.Message,"Patch 4 Go",MessageBoxButtons.OK,MessageBoxIcon.Error);notice.Text=ex.Message;}
 }
 void Connect(){Run(()=>{lock(gate){if(session.Engine?.Body?.Busy==true)throw new InvalidOperationException("Attends la fin de la réparation carrosserie.");if(session.Engine?.Protection?.NeedsCleanup==true)throw new InvalidOperationException("Désactive Jamais recherché avant de reconnecter.");session.Connect();snapshot=session.Engine.Inspect();workerError="";}status.Text="●  JEU CONNECTÉ";details.Text=session.Description;notice.Text="Plein, Remington et recul/précision testés. Jamais recherché disponible : prévention uniquement.";RefreshGame();});}
 void ApplyToggle(int index){
  try{lock(gate){if(session.Engine==null)return;session.Engine.God=toggles[0].On;session.Engine.Ammo=toggles[1].On;session.Engine.NoReload=toggles[2].On;session.Engine.Money=toggles[3].On;session.Engine.NoRecoil=toggles[4].On;var current=session.Engine.Inspect();Log("Options demandées : God="+session.Engine.God+" Ammo="+session.Engine.Ammo+" NoReload="+session.Engine.NoReload+" Money="+session.Engine.Money+" NoRecoil="+session.Engine.NoRecoil+" Player="+current.Player.ToString("X8")+" Car="+current.Car.ToString("X8")+" Weapon="+session.Engine.WeaponDiagnostic(current));session.Engine.Tick(current);}notice.Text=toggles.Count(t=>t.On)+" option(s) active(s) · Fonctionne en arrière-plan.";}
  catch(Exception ex){Stop();notice.Text=ex.Message;Log(ex.Message);}
 }
 void Stop(){lock(gate){if(session.Engine!=null)session.Engine.Stop();}foreach(var t in toggles){t.On=false;t.Invalidate();}notice.Text=session.Engine?.Protection?.NeedsCleanup==true?"Retrait de Jamais recherché en attente : reprends le jeu sans pause.":"Toutes les options sont désactivées.";}
 void WorkerTick(){
  if(closing)return;
  if(!System.Threading.Monitor.TryEnter(gate))return;
  try{
   if(closing||session.Engine==null)return;
   try{
    if(!session.Connected){Log("Fin du processus du jeu détectée ; NoRecoil="+session.Engine.NoRecoil+" God="+session.Engine.God+" Ammo="+session.Engine.Ammo);session.Dispose();snapshot=null;workerError="Le jeu est fermé. Relance-le puis reconnecte le trainer.";return;}
    Snapshot fresh=session.Engine.Inspect();session.Engine.Tick(fresh);snapshot=fresh;workerError="";workerTicks++;
   }catch(Exception ex){snapshot=null;if(workerError!=ex.Message)Log("Pause temporaire : "+ex.Message);workerError=ex.Message;try{session.Engine.Suspend();}catch{}}
  }finally{System.Threading.Monitor.Exit(gate);}
 }
 void RefreshGame(){
  lock(gate){
   bool ready=session.Engine!=null&&snapshot!=null&&session.Engine.Actions!=null;bool bodyBusy=session.Engine?.Body?.Busy==true;
   policeToggle.Enabled=ready&&!bodyBusy;repairVehicle.Enabled=ready&&!bodyBusy&&snapshot.Car!=0&&snapshot.Health>0;repairVehicle.Text=bodyBusy?"Réparation du véhicule · en cours":"Réparer le véhicule · moteur + carrosserie";refuel.Enabled=ready&&!bodyBusy&&snapshot.Car!=0&&snapshot.Health>0;give.Enabled=ready&&!bodyBusy&&(weapons.SelectedItem as WeaponEntry)?.GiveValidated==true;readCatalogue.Enabled=ready;
   readCars.Enabled=ready;spawnCar.Enabled=VehicleSpawner.Enabled&&ready&&!bodyBusy&&snapshot.Car==0&&(cars.SelectedItem as CarChoice)?.IsLoaded==true;
   if(ready){var protection=session.Engine.Protection;
    // L'interrupteur « Jamais recherché » porte l'intention de l'utilisateur et PERSISTE (y compris après la mort/respawn) : s'il est ON mais que la protection est retombée (mort, retrait par le jeu), on la re-demande dès que Vito est en vie. Si c'est le jeu qui a confirmé le retrait (mort), le toggle reste ON et se réapplique tout seul.
    if(policeToggle.On){if(!protection.Desired&&!protection.Pending&&!protection.NeedsCleanup&&snapshot.Health>0)protection.Request(true);}
    actionStatus.Text=protection.Status+"\n"+session.Engine.Body.Status+"\n"+session.Engine.Actions.LastStatus;if(closeWhenSafe&&!protection.NeedsCleanup&&!bodyBusy){closeWhenSafe=false;BeginInvoke(new Action(Close));}}
   // Réparation terminée : réactiver les cheats coupés automatiquement au lancement.
   if(repairPending&&session.Engine!=null&&!bodyBusy){repairPending=false;var eng=session.Engine;if(repairRestore!=null){eng.God=toggles[0].On=repairRestore[0];eng.Ammo=toggles[1].On=repairRestore[1];eng.NoReload=toggles[2].On=repairRestore[2];eng.Money=toggles[3].On=repairRestore[3];eng.NoRecoil=toggles[4].On=repairRestore[4];foreach(var t in toggles)t.Invalidate();}notice.Text="Véhicule réparé. "+eng.Body.Status;}
   if(session.Engine==null){foreach(var t in toggles){t.On=false;t.Enabled=false;t.Invalidate();}policeToggle.On=false;policeToggle.Enabled=false;policeToggle.Invalidate();addMoney.Enabled=false;status.Text="●  EN ATTENTE DU JEU";if(workerError!="")details.Text=workerError;return;}
   if(snapshot==null){status.Text="●  CHARGEMENT / PAUSE TEMPORAIRE";details.Text=workerError+" Les options choisies reprendront automatiquement.";return;}
   Snapshot fresh=snapshot;
   health.Text="SANTÉ  "+fresh.Health.ToString("0")+" / "+fresh.MaxHealth.ToString("0");wallet.Text=fresh.Wallet==0?"ARGENT  ·  Pas de portefeuille":"ARGENT  "+(fresh.Cents/100.0).ToString("N2")+" $";
   toggles[0].Enabled=fresh.Health>0&&!bodyBusy;toggles[1].Enabled=toggles[2].Enabled=toggles[4].Enabled=!bodyBusy;toggles[3].Enabled=addMoney.Enabled=fresh.Wallet!=0&&!bodyBusy;status.Text="●  JEU CONNECTÉ · ARRIÈRE-PLAN ACTIF";details.Text=session.Description;
  }
 }
 static void Log(string text){try{File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"trainer.log"),DateTime.Now.ToString("s")+" "+text+Environment.NewLine);}catch{}}
 public void VerifyBackground(){
  StartPosition=FormStartPosition.Manual;Location=new Point(-30000,-30000);Show();WindowState=FormWindowState.Minimized;
  Connect();if(session.Engine==null)throw new IOException("Connexion impossible.");
  lock(gate){session.Engine.Ammo=session.Engine.NoReload=session.Engine.NoRecoil=true;}
  int before;lock(gate)before=workerTicks;
  // Intentionally block the UI thread: only the background timer can maintain the options.
  System.Threading.Thread.Sleep(600);
  lock(gate){if(workerTicks-before<5||workerError!="")throw new IOException("Le traitement d'arrière-plan ne progresse pas : "+workerError);File.WriteAllText("background-check.txt","PASS: "+(workerTicks-before)+" cycles de maintien pendant 600 ms avec fenêtre réduite et thread UI bloqué.");session.Dispose();}
  Close();
 }
 public void Render(string file,int tab=0){StartPosition=FormStartPosition.Manual;Location=new Point(-30000,-30000);Show();var tabControl=Controls.OfType<DarkTabs>().FirstOrDefault();if(tabControl!=null)tabControl.SelectedIndex=Math.Max(0,Math.Min(tab,3));Application.DoEvents();using(var b=new Bitmap(Width,Height)){DrawToBitmap(b,new Rectangle(Point.Empty,Size));b.Save(file);}Hide();}

}
static class Program {
 [DllImport("kernel32.dll")] static extern bool FreeConsole();
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls,string title);
 [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
 [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
 [STAThread] static int Main(string[] args){
  if(args.Contains("--export-spawn")){File.WriteAllBytes("spawn-v04.bin",VehicleSpawner.Code(0x200000,0x400000));return 0;}
  if(args.Contains("--export-experimental")){File.WriteAllBytes("dispatcher-v04.bin",LuaActions.DispatchCode(0x90000,0x400000,0x10000));File.WriteAllBytes("precision-v04.bin",PlayerHooks.PrecisionCode(0x90002,0x90700,0x4208b5));return 0;}
  if(args.Contains("--live-check")){
   try{using(var session=new GameSession()){
    session.Connect();Snapshot before=session.Engine.Inspect();
    session.Engine.NoReload=session.Engine.NoRecoil=true;session.Engine.Tick(before);
    System.Threading.Thread.Sleep(200);
    Snapshot after=session.Engine.Inspect();session.Engine.Close();
    using(var process=Process.GetProcessesByName("mafia2").Single())using(var native=new NativeMemory(process.Id)){
     uint image=(uint)process.MainModule.BaseAddress.ToInt64();
     if(!native.Read(image+0x5a46e0,7).SequenceEqual(new byte[]{0x8b,0x41,0x08,0x8b,0x54,0x24,0x04})||!native.Read(image+0x20e60,5).SequenceEqual(new byte[]{0x53,0x56,0x57,0x8b,0xf1}))throw new IOException("Restauration à vérifier.");
    }
    File.WriteAllText("live-check.txt","PASS: activation/désactivation des routines sur la partie ouverte, instructions originales restaurées. Santé avant="+before.Health+" après="+after.Health+". Effet visuel au tir à vérifier en gameplay.");
   }return 0;}catch(Exception ex){File.WriteAllText("live-check.txt",ex.ToString());return 1;}
  }
  if(args.Contains("--export-hooks")){File.WriteAllBytes("ammo-hook.bin",PlayerHooks.AmmoCode(0x10000,0x90000,0x90100,0x9a46e7));File.WriteAllBytes("recoil-hook.bin",PlayerHooks.RecoilCode(0x10000,0x90001,0x90300,0x420e65));return 0;}
  if(args.Contains("--benchmark")){try{LiveTests.Run();return 0;}catch(Exception ex){File.WriteAllText("stability-check.txt",ex.ToString());return 1;}}
  if(args.Contains("--self-test")){try{Tests.Run();ExperimentalTests.Run();PoliceProtectionTests.Run();BodyRepairTests.Run();RecoilGuardTests.Run();GamePatchTests.Run();return 0;}catch(Exception e){File.WriteAllText("test-result.txt",e.ToString());return 1;}}
  if(args.Contains("--inspect")){try{using(var s=new GameSession()){s.Connect();Snapshot v=s.Engine.Inspect();File.WriteAllText("diagnostic.txt",s.Description+"\nSanté="+v.Health+"/"+v.MaxHealth+"\nChargeurs="+v.Clips.Count+" Réserves="+v.Reserves.Count+" Portefeuille="+(v.Wallet!=0));}return 0;}catch(Exception e){File.WriteAllText("diagnostic.txt",e.ToString());return 1;}}
  Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  if(args.Contains("--background-check")){try{using(var f=new TrainerForm(false))f.VerifyBackground();return 0;}catch(Exception ex){File.WriteAllText("background-check.txt",ex.ToString());return 1;}}
  bool preview=args.Length>=2&&args[0]=="--preview";
  if(preview){using(var form=new TrainerForm(true))form.Render(args[1],args.Length>2?int.Parse(args[2]):0);return 0;}
  FreeConsole();bool created;
  using(var instance=new System.Threading.Mutex(true,"Local\\EmpireBaySoloTrainer",out created)){
   if(!created){IntPtr window=FindWindow(null,"Empire Bay · Mafia II Solo Trainer");if(window!=IntPtr.Zero){ShowWindow(window,9);SetForegroundWindow(window);}return 0;}
   try{using(var form=new TrainerForm(false))Application.Run(form);}finally{instance.ReleaseMutex();}
  }return 0;
 }
}
sealed class FakeMemory:IMemory {
 public Dictionary<uint,byte> Bytes=new Dictionary<uint,byte>();
 public byte[] Read(uint a,int n){var result=new byte[n];for(int i=0;i<n;i++){byte b;if(!Bytes.TryGetValue(a+(uint)i,out b))throw new IOException("Unmapped");result[i]=b;}return result;}
 public void Write(uint a,byte[] b){for(int i=0;i<b.Length;i++)Bytes[a+(uint)i]=b[i];}
 public void U(uint a,uint v){Write(a,BitConverter.GetBytes(v));}
}
static class Tests {
 static void Check(bool b,string message){if(!b)throw new Exception(message);}
 public static void Run(){
  var m=new FakeMemory();uint root=0x10000,g=0x20000,p=0x30000,inv=0x40000,script=0x50000,slots=0x60000;
  m.U(root,g);m.U(g+0x118,p);m.Write(g+0x131,new byte[]{3});m.Write(p+0x22,new byte[]{5});m.U(p+0xa0,inv);m.U(p+0xa4,script);m.U(script,p);m.U(p+0x64,0);m.Write(p+0xd0,BitConverter.GetBytes(250f));m.Write(p+0xd4,BitConverter.GetBytes(500f));m.Write(p+0xea,new byte[]{0});m.U(inv+0x38,slots);m.U(inv+0x3c,slots+28);m.Write(inv+0xe8,new byte[]{0});m.U(0x90000+4,0x90100);m.Write(0x90100,new byte[]{0x33,0xc0,0xc3,0xcc,0xcc,0xcc});
  for(uint i=0;i<6;i++){uint bag=0x70000+i*0x100;m.U(slots+i*4,bag);m.U(bag+0x14,0);m.U(bag+0x18,0);}
  uint[] testSlots={0,4,5};foreach(uint i in testSlots){uint bag=0x70000+i*0x100,start=0x80000+i*0x100,reference=start+0x10,item=start+0x20,data=start+0x40;m.U(bag+0x14,start);m.U(bag+0x18,start+4);m.U(start,reference);m.U(reference+8,inv);m.U(reference+0xc,item);m.U(item+4,reference);m.U(item+8,data);m.U(data,0x90000);m.U(data+4,i==4?1015u:(i==5?2000u:15u));m.U(data+0xc,8);m.Write(data+0x10,BitConverter.GetBytes(10000L));}
  var engine=new Cheats(m,root);var s=engine.Inspect();Check(s.Clips.Count==1&&s.Reserves.Count==1&&s.Wallet!=0,"Inventory resolution");
  engine.God=engine.Ammo=engine.NoReload=engine.Money=true;engine.Tick(s);
  Check(m.Read(inv+0xe8,1)[0]==1,"Native unlimited ammo flag");Check(Values.F(m,p+0xd0)==500&&m.Read(p+0xea,1)[0]==1,"God mode");Check(Values.U(m,s.Reserves[0])==999,"Reserve");
  engine.Tick(engine.Inspect());Check(BitConverter.ToInt64(m.Read(s.Wallet,8),0)==99999900,"Wallet");
  m.Write(p+0xd0,BitConverter.GetBytes(504f));Check(engine.Inspect().Health==504,"Health above nominal max is valid");engine.Tick(engine.Inspect());Check(Values.F(m,p+0xd0)==504,"Do not lower over-max health");engine.Suspend();Check(engine.Ammo&&engine.NoReload,"Selections survive a transient loading state");engine.Tick(engine.Inspect());engine.Stop();Check(m.Read(inv+0xe8,1)[0]==0,"Restore ammo flag");Check(m.Read(p+0xea,1)[0]==0,"Restore god flag");
  Snapshot moneySnapshot=engine.Inspect();m.U(moneySnapshot.WalletReference+8,0x45000);bool walletRefused=false;try{engine.SetMoney(moneySnapshot,50);}catch(InvalidDataException){walletRefused=true;}Check(walletRefused,"Reject stale/foreign wallet before writing");m.U(moneySnapshot.WalletReference+8,inv);
  m.U(g+0x118,0);bool refused=false;try{engine.Inspect();}catch(InvalidDataException){refused=true;}Check(refused,"Reject menu pointer");
  using(var native=new NativeMemory(Process.GetCurrentProcess().Id)){IntPtr block=Marshal.AllocHGlobal(4);try{native.WriteAddress(block.ToInt64(),BitConverter.GetBytes(12345));Check(BitConverter.ToInt32(native.ReadAddress(block.ToInt64(),4),0)==12345,"Win32 round trip");}finally{Marshal.FreeHGlobal(block);}}
  var patches=new FakePatchMemory();patches.Write(0x9a46e0,new byte[]{0x8b,0x41,0x08,0x8b,0x54,0x24,0x04});patches.Write(0x420e60,new byte[]{0x53,0x56,0x57,0x8b,0xf1});
  var hooks=new PlayerHooks(patches,root,0x400000);hooks.Set(true,true,p,inv);int writes=patches.Writes;for(int i=0;i<100;i++)hooks.Set(true,true,p,inv);Check(patches.Writes==writes,"Unchanged hook state performs no writes");hooks.Set(false,false);hooks.Dispose();Check(patches.Read(0x9a46e0,7).SequenceEqual(new byte[]{0x8b,0x41,0x08,0x8b,0x54,0x24,0x04})&&patches.Read(0x420e60,5).SequenceEqual(new byte[]{0x53,0x56,0x57,0x8b,0xf1}),"Restore original instructions");
  // Régression cinématique : après Dispose (suspension transition/MG42), une réactivation doit RÉARMER les drapeaux du cave (sinon hooks réinstallés mais inertes = bouton allumé sans effet).
  hooks.Set(true,true,p,inv);Check(patches.Read(hooks.FlagAddress,3)[0]==1,"Cave armé (chargeur) après activation");
  hooks.Dispose();Check(patches.Read(hooks.FlagAddress,1)[0]==0,"Cave désarmé par Dispose");
  hooks.Set(true,true,p,inv);Check(patches.Read(hooks.FlagAddress,3)[0]==1,"Réactivation après Dispose réarme le cave (régression chargeur post-cinématique)");hooks.Dispose();
  // Suspension automatique pendant une transition scriptée (joueur +0x22 bit 0x20 mis = pas en contrôle) : aucune écriture de cheat.
  m.U(g+0x118,p);engine.God=true;Snapshot cs=engine.Inspect();m.Write(p+0xea,new byte[]{0});
  m.Write(p+0x22,new byte[]{0x25});engine.Tick(cs);Check(m.Read(p+0xea,1)[0]==0,"Hors contrôle : aucune écriture de cheat (suspension transitions)");
  File.WriteAllText("test-result.txt","PASS: player and inventory resolution, health, flag restoration, ammo, clip, wallet, invalid pointers, native memory API, suspension hors-contrôle.");
 }
}
sealed class FakePatchMemory:IPatchMemory {
 readonly FakeMemory memory=new FakeMemory();public int Writes;
 public byte[] Read(uint a,int n){return memory.Read(a,n);}public void Write(uint a,byte[] b){Writes++;memory.Write(a,b);}
 public uint Allocate(int count){return 0x100000;}public void Patch(uint a,byte[] b){Write(a,b);}
}
static class LiveTests {
 public static void Run(){
  string backup=Backups.Create();var report=new List<string>{"Empire Bay 0.3 — test local de stabilité et de coût", "Copie des sauvegardes : "+backup};
  using(var session=new GameSession()){
   session.Connect();Snapshot baseline=session.Engine.Inspect();long originalMoney=baseline.Cents;bool moneyChanged=false;long expectedMoney=originalMoney;
   try{
    if(baseline.Wallet!=0){
     session.Engine.AddMoney(baseline);Snapshot changed=session.Engine.Inspect();expectedMoney=originalMoney+1000000;moneyChanged=true;
     if(changed.Wallet!=baseline.Wallet||changed.Cents!=expectedMoney)throw new IOException("Le test d'ajout d'argent ne correspond pas à la valeur attendue.");
     report.Add("Argent : "+(originalMoney/100.0).ToString("F2")+" -> "+(changed.Cents/100.0).ToString("F2")+" $ ; ajout de 10 000 $ vérifié.");
     session.Engine.SetMoney(changed,originalMoney);moneyChanged=false;
    }
    session.Engine.God=session.Engine.Ammo=session.Engine.NoReload=session.Engine.NoRecoil=session.Engine.Money=true;
    session.Engine.Tick(session.Engine.Inspect());if(baseline.Wallet!=0){expectedMoney=99999900;moneyChanged=true;}
    var timings=new List<double>();using(var self=Process.GetCurrentProcess()){
     TimeSpan cpuBefore=self.TotalProcessorTime;long readsBefore=session.ReadCalls,writesBefore=session.WriteCalls;var wall=Stopwatch.StartNew();
     while(wall.ElapsedMilliseconds<20000){
      if(!session.Connected)throw new IOException("Le jeu s'est fermé pendant le test.");
      var tick=Stopwatch.StartNew();Snapshot fresh=session.Engine.Inspect();session.Engine.Tick(fresh);timings.Add(tick.Elapsed.TotalMilliseconds);
      System.Threading.Thread.Sleep(100);
     }
     self.Refresh();double cpuMs=(self.TotalProcessorTime-cpuBefore).TotalMilliseconds;
     timings.Sort();report.Add("Partie connectée pendant "+wall.Elapsed.TotalSeconds.ToString("F1")+" s, toutes les options activées ; "+timings.Count+" cycles sans erreur.");
     report.Add("CPU du trainer : "+(cpuMs/wall.Elapsed.TotalMilliseconds*100).ToString("F3")+" % d'un cœur logique ; moyenne "+timings.Average().ToString("F3")+" ms/cycle ; p95 "+timings[(int)(timings.Count*.95)].ToString("F3")+" ms ; maximum "+timings.Last().ToString("F3")+" ms.");
     report.Add("Lectures : "+(session.ReadCalls-readsBefore)+" ; écritures : "+(session.WriteCalls-writesBefore)+" pendant le maintien.");
    }
    session.Engine.Stop();
    if(baseline.Wallet!=0){Snapshot current=session.Engine.Inspect();if(current.Wallet==baseline.Wallet&&current.Cents==expectedMoney){session.Engine.SetMoney(current,originalMoney);moneyChanged=false;report.Add("Argent illimité vérifié en mémoire ; montant initial rétabli : "+(originalMoney/100.0).ToString("F2")+" $.");}else report.Add("Portefeuille modifié pendant le test : aucun écrasement du nouvel état.");}
    session.Engine.Close();report.Add("Options arrêtées et routines restaurées. Le coût GPU/FPS et chaque arme au tir ne sont pas mesurés par ce test.");
   }finally{
    try{session.Engine.Stop();if(moneyChanged){Snapshot now=session.Engine.Inspect();if(now.Wallet==baseline.Wallet&&now.Cents==expectedMoney)session.Engine.SetMoney(now,originalMoney);}}catch{}
    File.WriteAllLines("stability-check.txt",report);
   }
  }
 }
}
}
















