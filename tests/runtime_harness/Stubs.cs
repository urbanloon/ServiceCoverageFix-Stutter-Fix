// Test-only adapters. Deterministic deferred scheduling and native allocation
// stand-ins exercise production bridge/lifecycle code outside Unity. These do
// not emulate Burst, Harmony patch installation, or Unity's safety system.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
namespace Unity.Burst {
 public enum OptimizeFor { Performance }
 [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Method)] public sealed class BurstCompileAttribute : Attribute { public OptimizeFor OptimizeFor {get;set;} }
 [AttributeUsage(AttributeTargets.Method)] public sealed class BurstDiscardAttribute : Attribute {}
}
namespace Unity.Burst.CompilerServices {
 [AttributeUsage(AttributeTargets.Method)] public sealed class SkipLocalsInitAttribute : Attribute {}
 [AttributeUsage(AttributeTargets.Parameter)] public sealed class NoAliasAttribute : Attribute {}
}
namespace Unity.Collections {
 public enum Allocator { Persistent, TempJob }
 [AttributeUsage(AttributeTargets.Field)] public sealed class ReadOnlyAttribute : Attribute {}
 public unsafe struct NativeArray<T> : IDisposable where T: unmanaged {
  private T* ptr;
  public NativeArray(int length, Allocator allocator) {ptr=(T*)NativeMemory.AllocZeroed((nuint)(Math.Max(1,length)*sizeof(T))); AllocationTracker.Live++;}
  public bool IsCreated => ptr!=null;
  public T this[int i] {get=>ptr[i];set=>ptr[i]=value;}
  public void* Ptr => ptr;
  public void Dispose(){if(ptr!=null){NativeMemory.Free(ptr);ptr=null;AllocationTracker.Live--;}}
 }
 public static class AllocationTracker {public static int Live;}
 public unsafe struct NativeList<T> : IDisposable where T: unmanaged {
  private struct Header {public T* Data;public int Length;public int Capacity;}
  private Header* header;
  public NativeList(int capacity, Allocator allocator){header=(Header*)NativeMemory.AllocZeroed((nuint)sizeof(Header));header->Capacity=Math.Max(1,capacity);header->Data=(T*)NativeMemory.AllocZeroed((nuint)(header->Capacity*sizeof(T)));AllocationTracker.Live++;}
  public int Length=>header->Length;
  public void* Ptr=>header->Data;
  public T this[int i]{get=>header->Data[i];set=>header->Data[i]=value;}
  public void Add(T value){if(header->Length==header->Capacity)throw new InvalidOperationException("Test list capacity exceeded");header->Data[header->Length++]=value;}
  public void RemoveAtSwapBack(int i){header->Data[i]=header->Data[--header->Length];}
  public void Sort<TC>(TC comparer) where TC:IComparer<T>{var values=new T[Length];for(int i=0;i<Length;i++)values[i]=this[i];Array.Sort(values,comparer);for(int i=0;i<Length;i++)this[i]=values[i];}
  public void Dispose(){if(header!=null){NativeMemory.Free(header->Data);NativeMemory.Free(header);header=null;AllocationTracker.Live--;}}
 }
}
namespace Unity.Collections.LowLevel.Unsafe {
 [AttributeUsage(AttributeTargets.Field)] public sealed class NativeDisableUnsafePtrRestrictionAttribute:Attribute{}
 [AttributeUsage(AttributeTargets.Field)] public sealed class NativeDisableContainerSafetyRestrictionAttribute:Attribute{}
 public static unsafe class UnsafeUtility {
  public static int SizeOf<T>() where T:unmanaged=>sizeof(T);
  public static void* AddressOf<T>(ref T value) where T:unmanaged=>System.Runtime.CompilerServices.Unsafe.AsPointer(ref value);
  public static void MemCpy(void* dst,void* src,long size)=>Buffer.MemoryCopy(src,dst,size,size);
 }
 public static unsafe class NativeArrayUnsafeUtility {public static void* GetUnsafePtr<T>(Unity.Collections.NativeArray<T> a) where T:unmanaged=>a.Ptr;}
 public static unsafe class NativeListUnsafeUtility {
  public static void* GetUnsafePtr<T>(Unity.Collections.NativeList<T> a) where T:unmanaged=>a.Ptr;
  public static void* GetUnsafeReadOnlyPtr<T>(Unity.Collections.NativeList<T> a) where T:unmanaged=>a.Ptr;
 }
}
namespace Unity.Jobs {
 public interface IJob {void Execute();}
 internal sealed class Work {internal Action Action=null!;internal bool Done;}
 public struct JobHandle {
  internal Work? Work;
  public bool IsCompleted=>Work==null||Work.Done;
  public void Complete(){if(Work!=null&&!Work.Done){Work.Action();Work.Done=true;}}
 }
 public static class IJobExtensions {
  public static bool FailNext;
  public static int Schedules;
  public static JobHandle Schedule<T>(T job,JobHandle dependency) where T:struct,IJob {
   if(FailNext){FailNext=false;throw new InvalidOperationException("Injected schedule failure");}
   Schedules++;return new JobHandle{Work=new Work{Action=()=>{dependency.Complete();job.Execute();}}};
  }
 }
}
namespace HarmonyLib {
 public sealed class HarmonyPatch:Attribute{}
 public static class AccessTools {public static MethodInfo? Method(Type t,string name)=>t.GetMethod(name,BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);}
 public sealed class CodeInstruction {public OpCode opcode;public object? operand;}
}
namespace ServiceCoverageFix {
 internal static class CompatibilityGate {internal static Type ServiceCoverageSystemType=>typeof(object);internal const string ServiceCoverageSystemName="Test";internal const string ApplyCoverageJobName="TestJob";}
 public static class Mod {public static readonly TestLogger Log=new TestLogger();}
 public sealed class TestLogger {public void Info(object s){} public void Error(object s){} }
}
