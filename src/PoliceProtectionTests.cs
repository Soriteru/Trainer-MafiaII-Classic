using System;
using System.Linq;
namespace EmpireBay {
static class PoliceProtectionTests {
 static void Check(bool b,string s){if(!b)throw new Exception(s);}
 public static void Run(){
  var m=new FakePatchMemory();Action<uint,uint> u=(a,v)=>m.Write(a,BitConverter.GetBytes(v));uint image=0x400000,root=0x10000,player=0x30000,guid=0x2410;
  m.Write(image+0x3bd42,LuaActions.Original(image));m.Write(image+0x1d5bd0,new byte[]{0x55,0x8b,0xec});m.Write(image+0x1d3f80,new byte[]{0x55,0x8b,0xec});
  u(root,0x20000);u(0x20118,player);u(player+0x1c,guid);u(image+0x16c0c78,0xa0000);u(0xa0004,0xa0100);u(0xa0104,0xa0200);u(0xa0200,0xa0300);u(0xa0350,0xa0400);
  u(image+0x18c80e0,0x50000);u(0x50004,0x50100);m.Write(0x50100,new byte[32]);m.Write(0x50115,new byte[]{1});u(0x50104,0x50200);m.Write(0x50200,new byte[32]);u(0x5020c,4);u(0x50210,0x51000);
  u(0x51048,0x51100);u(0x5104c,0);m.Write(0x51100,new byte[32]);m.Write(0x51111,new byte[]{1});u(0x51104,0x51100);
  Action<bool> ignored=value=>{u(0x51104,value?0x854daaf8u:0x51100u);u(0x5104c,value?1u:0u);m.Write(0x854daaf8,new byte[32]);u(0x854dab04,guid);};
  var s=new Snapshot{Player=player,Health=100};using(var actions=new LuaActions(m,image,root)){
   var protection=new PoliceProtection(m,actions,image);protection.Request(true);protection.Tick(s);Check(protection.Pending&&!protection.Applied,"Activation is pending until engine confirmation");
   ignored(true);u(0x100000,3);actions.Poll();protection.Tick(s);Check(protection.Applied&&!protection.Pending,"Activation verified against actual per-person list");Check(m.Read(image+0x3bd42,5).SequenceEqual(LuaActions.Original(image)),"Input callback removed after activation");
   protection.Request(false);protection.Tick(s);Check(protection.Pending&&protection.NeedsCleanup,"Shutdown must wait for confirmed removal");
   ignored(false);u(0x100000,3);actions.Poll();protection.Tick(s);Check(!protection.Applied&&!protection.NeedsCleanup,"Removal verified before allowing shutdown");
   ignored(true);protection.Request(true);protection.Tick(s);Check(protection.Pending,"Activation proceeds even if Vito already ignored (to clear the active search)");Check(!m.Read(image+0x3bd42,5).SequenceEqual(LuaActions.Original(image)),"Game action queued to clear the search when already ignored");
   u(0x100000,3);actions.Poll();protection.Tick(s);Check(protection.Applied&&!protection.Pending,"Confirmed applied when already ignored");protection.Request(false);protection.Tick(s);ignored(false);u(0x100000,3);actions.Poll();protection.Tick(s);Check(!protection.Applied&&!protection.NeedsCleanup,"Can disable afterwards");
   ignored(false);protection.Request(true);protection.Tick(s);m.Write(0x100028,new byte[256]);u(0x100000,4);actions.Poll();protection.Tick(s);Check(!protection.Desired&&!protection.NeedsCleanup,"Refused activation does not leave protection selected");
  }
 }
}
}
