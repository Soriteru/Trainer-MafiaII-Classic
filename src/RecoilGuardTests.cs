using System;
namespace EmpireBay {
static class RecoilGuardTests {
 public static void Run(){
  var m=new FakePatchMemory();Action<uint,uint> u=(a,v)=>m.Write(a,BitConverter.GetBytes(v));uint image=0x400000;
  u(image+0x16b5a60,0x20000);u(0x20020,0x21000);u(0x21004,0x22000);u(0x22008,3);u(0x22014,0x40);u(0x2201c,0x30000);u(0x30000,4);u(0x30040,8);u(0x30080,11);
  m.Write(0x9a46e0,new byte[]{0x8b,0x41,0x08,0x8b,0x54,0x24,0x04});m.Write(0x420e60,new byte[]{0x53,0x56,0x57,0x8b,0xf1});
  uint start=0x30000,end=0x30000+3*0x40;
  // Une table d'armes non reconnue doit être refusée, pas devinée.
  using(var bad=new PlayerHooks(m,0x10000,image)){u(0x22014,0x10);bool refused=false;try{bad.ConfigureValidatedRecoilRows();}catch(System.IO.InvalidDataException){refused=true;}u(0x22014,0x40);if(!refused)throw new Exception("Unrecognized weapon table accepted");}
  using(var h=new PlayerHooks(m,0x10000,image)){
   h.ConfigureValidatedRecoilRows();h.Set(false,true,0x50000,0x60000);
   if(Values.U(m,h.FlagAddress+12)!=start)throw new Exception("Weapon table start not published");
   if(Values.U(m,h.FlagAddress+16)!=end)throw new Exception("Weapon table end not published");
   h.Set(false,true,0x51000,0x61000);if(Values.U(m,h.FlagAddress+12)!=start||Values.U(m,h.FlagAddress+16)!=end)throw new Exception("Player identity update damaged bounds");
  }
 }
}
}
