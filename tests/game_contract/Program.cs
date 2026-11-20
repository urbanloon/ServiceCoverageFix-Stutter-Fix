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
// A CLR type with the same full name in a second assembly is a different job.
// Another transpiler can remove the original site or add a foreign Schedule call.
var shadowModule = AssemblyBuilder.DefineDynamicAssembly(
    new AssemblyName("ForeignCoverageJobs"), AssemblyBuilderAccess.Run).DefineDynamicModule("ForeignCoverageJobs");
var shadowList = shadowModule.DefineType("Unity.Collections.NativeList`1", TypeAttributes.Public |
    TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
shadowList.DefineGenericParameters("T");
var wrongList = shadowList.CreateType()!;
var wrongLayout = shadowModule.DefineType("ForeignListHolder", TypeAttributes.Public);
var building = game.GetType("Game.Simulation.ServiceCoverageSystem+BuildingData", true)!;
wrongLayout.DefineField("m_BuildingData", wrongList.MakeGenericType(building), FieldAttributes.Public);
bool rejectedHeader = false;
try { gate.GetMethod("ValidateNativeListField", methods)!.Invoke(null,
    new object[]{ wrongLayout.CreateType()!, "m_BuildingData", "BuildingData", 24 }); }
catch (TargetInvocationException e) when (e.InnerException?.GetType().Name == "CoverageContractMismatch")
{ rejectedHeader = true; }
if (!rejectedHeader) throw new InvalidDataException("An unrelated native container header passed the layout gate.");
var shadowSystem = shadowModule.DefineType("Game.Simulation.ServiceCoverageSystem", TypeAttributes.Public);
Type CloneJob(string name)
{
    var gameJob = game.GetType("Game.Simulation.ServiceCoverageSystem+" + name, true)!;
    var job = shadowSystem.DefineNestedType(name, TypeAttributes.NestedPublic | TypeAttributes.Sealed |
        TypeAttributes.SequentialLayout, typeof(ValueType));
    var contract = gameJob.GetInterfaces().Single(t => t.FullName == "Unity.Jobs.IJob");
    job.AddInterfaceImplementation(contract);
    var execute = job.DefineMethod("Execute", MethodAttributes.Public | MethodAttributes.Virtual,
        typeof(void), Type.EmptyTypes);
    execute.GetILGenerator().Emit(OpCodes.Ret);
    if (name == "PrepareCoverageJob")
        job.DefineField("m_Service", game.GetType("Game.Net.CoverageService", true)!, FieldAttributes.Public);
    return job.CreateType()!;
}
var foreignPrepare = CloneJob("PrepareCoverageJob");
var foreignApply = CloneJob("ApplyCoverageJob");
shadowSystem.CreateType();

foreach (bool preview in new[] { false, true })
{
var system = preview ? "Game.Tools.CoveragePreviewSystem" : "Game.Simulation.ServiceCoverageSystem";
var entry = preview ? "SchedulePreview" : "ScheduleOptimized";
var update = game.GetType(system,true)!.GetMethod("OnUpdate",methods)!;
var patch = mod.GetType(preview ? "ServiceCoverageFix.ServiceCoveragePreviewPatch" : "ServiceCoverageFix.ServiceCoverageSystemPatch",true)!.GetMethod("Transpiler",methods)!;
HarmonyEmission.Check(update, patch, entry);
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
var foreignCall = new CodeInstruction(OpCodes.Call, original.GetGenericMethodDefinition().MakeGenericMethod(foreignApply));
var foreignOnly = Fresh();
foreignOnly[apply] = foreignCall;
bool foreignRejected = false;
try { Transform(foreignOnly); } catch (InvalidOperationException) { foreignRejected = true; }
Require(foreignRejected, "A same-named Apply job from a different assembly bypassed preflight.");
var additionalCall = Fresh();
additionalCall.Insert(apply + 1, foreignCall);
Require(Transform(additionalCall).Contains(foreignCall), "An unrelated schedule site was replaced.");
var foreignCategory = Fresh();
var prepareSchedule = (MethodInfo)foreignCategory[prepare].operand;
foreignCategory[prepare].operand = prepareSchedule.GetGenericMethodDefinition().MakeGenericMethod(foreignPrepare);
if (preview)
{
    bool rejected = false;
    try { Transform(foreignCategory); } catch (InvalidOperationException) { rejected = true; }
    Require(rejected, "Preview accepted attribution from a foreign Prepare job.");
}
else
{
    var transformed = Transform(foreignCategory);
    int call = transformed.FindIndex(i => i.operand is MethodInfo m && m.Name == entry);
    Require(call > 0 && transformed[call - 1].opcode == OpCodes.Ldc_I4_M1,
        "Simulation attributed a foreign Prepare job's category.");
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
Console.WriteLine($"PASS real {system}.OnUpdate, Prepare service field, unchanged intervening calls and dependencies, emitted Harmony bridge token, zero/duplicate and foreign job rejection, uncertain attribution fallback.");
}
