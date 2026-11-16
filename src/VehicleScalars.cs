using System;
using System.IO;
namespace EmpireBay {
// One-shot fuel update. No Lua call, callback, allocation or physics reconstruction.
sealed class VehicleScalars {
 readonly IMemory memory;readonly uint root;
 public VehicleScalars(IMemory m,uint r){memory=m;root=r;}
 public sealed class FuelState {public uint Player,Car,Header,Entries,Component;public float Current,Capacity;}
 static bool Finite(float v){return !float.IsNaN(v)&&!float.IsInfinity(v);}
 public FuelState InspectFuel(){
  uint player=Values.Ptr(memory,Values.Ptr(memory,root)+0x118);
  uint script=Values.Ptr(memory,player+0xa4);
  if(Values.Ptr(memory,script)!=player||Values.F(memory,player+0xd0)<=0)throw new InvalidDataException("Joueur indisponible.");
  uint car=Values.Ptr(memory,player+0x64),header=Values.Ptr(memory,car+0x2ec),entries=Values.Ptr(memory,header+8),end=Values.U(memory,header+12);
  if(end<=entries||end-entries>4096||(end-entries)%8!=0)throw new InvalidDataException("Réservoir non reconnu.");
  uint component=Values.Ptr(memory,entries);float current=Values.F(memory,component+0x1cc),capacity=Values.F(memory,component+0x1d0);
  if(!Finite(current)||!Finite(capacity)||capacity<1||capacity>1000||current<0||current>capacity+0.01f)throw new InvalidDataException("Valeurs de carburant non reconnues.");
  return new FuelState{Player=player,Car=car,Header=header,Entries=entries,Component=component,Current=current,Capacity=capacity};
 }
 public FuelState Fill(FuelState expected){
  var fresh=InspectFuel();
  if(expected==null||fresh.Player!=expected.Player||fresh.Car!=expected.Car||fresh.Header!=expected.Header||fresh.Entries!=expected.Entries||fresh.Component!=expected.Component||fresh.Capacity!=expected.Capacity)throw new InvalidOperationException("Le véhicule a changé : plein annulé.");
  memory.Write(fresh.Component+0x1cc,BitConverter.GetBytes(fresh.Capacity));
  return InspectFuel();
 }
}
}
