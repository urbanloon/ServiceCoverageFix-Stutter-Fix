using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;

if (args.Length != 2) throw new ArgumentException("GameContract <game Managed directory> <candidate DLL>");
string managed = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (ctx, name) => {
    var path = Path.Combine(managed, name.Name + ".dll");
    return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
};
var game = Assembly.LoadFrom(Path.Combine(managed,"Game.dll"));
var mod = Assembly.LoadFrom(Path.GetFullPath(args[1]));
var gate = mod.GetType("ServiceCoverageFix.CompatibilityGate",true)!;
const BindingFlags methods = BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
gate.GetMethod("Validate",methods)!.Invoke(null,null);
foreach (bool preview in new[] { false, true })
{
var system = preview ? "Game.Tools.CoveragePreviewSystem" : "Game.Simulation.ServiceCoverageSystem";
var entry = preview ? "SchedulePreview" : "ScheduleOptimized";
var update = game.GetType(system,true)!.GetMethod("OnUpdate",methods)!;
var patch = mod.GetType(preview ? "ServiceCoverageFix.ServiceCoveragePreviewPatch" : "ServiceCoverageFix.ServiceCoverageSystemPatch",true)!.GetMethod("Transpiler",methods)!;
var emit = new DynamicMethod("SimulatePatch",typeof(void),Type.EmptyTypes).GetILGenerator();
foreach(var local in update.GetMethodBody()!.LocalVariables) emit.DeclareLocal(local.LocalType,local.IsPinned);
var source = Reader.Read(update,emit);
List<CodeInstruction> Transform(List<CodeInstruction> input)
{
    try { return ((IEnumerable<CodeInstruction>)patch.Invoke(null,new object[]{input,emit})!).ToList(); }
    catch(TargetInvocationException e) when(e.InnerException is not null) { throw e.InnerException; }
}
void Require(bool value,string message) { if (!value) throw new InvalidDataException(message); }
bool IsJob(CodeInstruction instruction, string job) => instruction.operand is MethodInfo method &&
    method.Name == "Schedule" && method.GetGenericArguments().FirstOrDefault()?.Name==job;
List<CodeInstruction> Fresh() => source.Select(i => new CodeInstruction(i)).ToList();
int prepare=source.FindIndex(i=>IsJob(i,"PrepareCoverageJob")),apply=source.FindIndex(i=>IsJob(i,"ApplyCoverageJob"));
Require(prepare>=0&&apply>prepare,"Could not locate jobs in original method.");
var output=Transform(Fresh());
Require(output.Count==source.Count+6,"Unexpected count.");
Require(output[prepare].opcode==OpCodes.Stloc && output[prepare+1].opcode==OpCodes.Dup &&
    output[prepare+2].opcode==OpCodes.Ldfld && output[prepare+2].operand is FieldInfo field &&
    field.DeclaringType!.Name=="PrepareCoverageJob" && field.Name=="m_Service" && field.FieldType.FullName=="Game.Net.CoverageService" &&
    output[prepare+3].opcode==OpCodes.Stloc && output[prepare+4].opcode==OpCodes.Ldloc &&
    Equals(output[prepare].operand,output[prepare+4].operand) &&
    ((LocalBuilder)output[prepare].operand).LocalType.FullName=="Unity.Jobs.JobHandle" &&
    ((LocalBuilder)output[prepare+3].operand).LocalType==typeof(int),"Prepare capture invalid.");
int insert=apply+5;
var original=(MethodInfo)source[apply].operand;
var replacement=(MethodInfo)output[insert+1].operand;
Require(output[insert].opcode==OpCodes.Ldloc && Equals(output[insert].operand,output[prepare+3].operand) &&
    output[insert+1].opcode==OpCodes.Call && replacement.Name==entry &&
    original.ReturnType==replacement.ReturnType && original.GetParameters().Select(p=>p.ParameterType).Append(typeof(int))
        .SequenceEqual(replacement.GetParameters().Select(p=>p.ParameterType)),"Apply signature or attribution invalid.");
// Remove just the two injected sequences and compare the complete original method,
// including its original branches, resolved member references, and call order.
output[insert+1]=new CodeInstruction(source[apply]);
output.RemoveAt(insert);
output.RemoveRange(prepare,5);
for(int i=0;i<source.Count;i++)
    Require(source[i].opcode==output[i].opcode && Equals(source[i].operand,output[i].operand),"Unexpected change at "+i);
foreach(var count in new[]{0,2})
{
    var test=Fresh();
    if(count==0)test.RemoveAt(apply);else test.Insert(apply,new CodeInstruction(source[apply]));
    bool rejected=false;
    try { Transform(test); }catch(InvalidOperationException){rejected=true;}
    Require(rejected,"Ambiguous Apply site accepted.");
}
foreach(bool alternateEntry in new[]{false,true})
{
    var test=Fresh();
    if(alternateEntry)test[apply].labels.Add(emit.DefineLabel());else test.RemoveAt(prepare);
    if (preview)
    {
        bool rejected=false;
        try { Transform(test); }catch(InvalidOperationException){ rejected=true; }
        Require(rejected,"Preview ran without reliable category binding.");
        continue;
    }
    var result=Transform(test);
    int call=result.FindIndex(i=>i.operand is MethodInfo m&&m.Name=="ScheduleOptimized");
    Require(call>0&&result[call-1].opcode==OpCodes.Ldc_I4_M1,"Uncertain category was attributed.");
}
Console.WriteLine($"PASS real {system}.OnUpdate, Prepare service field, unchanged intervening calls and dependencies, bridge signature, zero/duplicate Apply rejection, uncertain attribution fallback.");
}
