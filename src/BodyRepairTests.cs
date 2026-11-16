using System;
using System.Linq;
namespace EmpireBay {
static class BodyRepairTests {
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 public static void Run(){
  var memory=new FakePatchMemory();uint image=0x400000,root=0x10000,player=0x30000,car=0x826ede40;
  Action<uint,uint> u=(a,v)=>memory.Write(a,BitConverter.GetBytes(v));
  memory.Write(image+0x3bd42,LuaActions.Original(image));memory.Write(image+0x1d5bd0,new byte[]{0x55,0x8b,0xec});memory.Write(image+0x1d3f80,new byte[]{0x55,0x8b,0xec});
  u(root,0x20000);u(0x20118,player);u(player+0x64,car);u(car+0x1c,123);u(image+0x16c0c78,0xa0000);u(0xa0004,0xa0100);u(0xa0104,0xa0200);u(0xa0200,0xa0300);u(0xa0350,0xa0400);
  var s=new Snapshot{Player=player,Car=car,Health=500};DateTime clock=DateTime.UtcNow;
  using(var actions=new LuaActions(memory,image,root)){
   var body=new BodyRepair(memory,actions,()=>clock);bool refused=false;s.Car=0;try{body.Repair(s);}catch(InvalidOperationException){refused=true;}Check(refused&&!body.Busy,"Refuse absent car");s.Car=car;
   body.Repair(s);Check(body.Busy,"Wait for scheduler submission");refused=false;try{body.Repair(s);}catch(InvalidOperationException){refused=true;}Check(refused,"Prevent double submission");
   u(0x100000,3);actions.Poll();body.Tick(s);Check(body.Busy,"Submission success is not repair completion");Check(memory.Read(image+0x3bd42,5).SequenceEqual(LuaActions.Original(image)),"Submission callback removed before deferred repair");
   clock=clock.AddSeconds(2);body.Tick(s);Check(actions.ExecutionState==1,"Completion query queued separately");u(0x100000,3);actions.Poll();body.Tick(s);Check(!body.Busy&&body.Status.Contains("terminée"),"Only completion query confirms repair");
   body.Repair(s);u(0x100000,4);memory.Write(0x100028,new byte[256]);actions.Poll();body.Tick(s);Check(!body.Busy&&body.Status.Contains("non confirmé"),"Rejected submission never reports success");
   u(player+0x64,car+16);refused=false;try{body.Repair(s);}catch(InvalidOperationException){refused=true;}Check(refused,"Reject stale occupied vehicle");
  }
 }
}
}
