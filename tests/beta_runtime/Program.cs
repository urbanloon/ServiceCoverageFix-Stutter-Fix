using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Unity.Jobs;
using HarmonyLib;
namespace ServiceCoverageFix
{
    internal static class Program
    {
        static void Check(bool pass,string reason) { if(!pass)throw new Exception(reason); }
        static JobHandle Submit(BetaFeature feature, JobHandle dep, int id) => feature switch {
            BetaFeature.Water => BetaSchedule.Water(new Game.Simulation.WaterPipeFlowJob{Id=id},dep),
            BetaFeature.CompanyCommercial => BetaSchedule.CompanyCommercial(new Game.Buildings.PropertyUtils.CompanyFindPropertyJob{Id=id},dep),
            BetaFeature.CompanyIndustrial => BetaSchedule.CompanyIndustrial(new Game.Buildings.PropertyUtils.CompanyFindPropertyJob{Id=id},dep),
            BetaFeature.Route => BetaSchedule.Route(new Game.Pathfind.PathfindQueueSystem.PathfindWorkerJob{Id=id},dep),
            _ => throw new ArgumentOutOfRangeException(nameof(feature)) };
        static Type[] custom={typeof(WaterJob),typeof(RouteJob),typeof(CompanyPropertyJob),typeof(CompanyPropertyJob)};
        static Type[] stock={typeof(Game.Simulation.WaterPipeFlowJob),typeof(Game.Pathfind.PathfindQueueSystem.PathfindWorkerJob),typeof(Game.Buildings.PropertyUtils.CompanyFindPropertyJob),typeof(Game.Buildings.PropertyUtils.CompanyFindPropertyJob)};
        static void Masks()
        {
            var sites = new[] { BetaFeature.Water, BetaFeature.Route, BetaFeature.CompanyCommercial, BetaFeature.CompanyIndustrial };
            Check(Enum.GetValues<BetaFeature>().SequenceEqual(sites), "unexpected patch site");
            Check(BetaFeatures.Options.SequenceEqual(sites.Take(3)), "unexpected option");
            for (int mask = 0; mask < 8; mask++)
            {
                var rt = new BetaRuntime(BetaRuntime.GameHash);
                var dep = new JobHandle { Work = new Work() };
                bool Enabled(BetaFeature site)
                {
                    var option = site == BetaFeature.CompanyIndustrial ? BetaFeature.CompanyCommercial : site;
                    return (mask & (1 << (int)option)) != 0;
                }
                foreach (var option in BetaFeatures.Options) rt.Set(option, Enabled(option));
                var expected = sites.Where(Enabled).ToDictionary(BetaPatches.Transpiler,
                    site => Mod.HarmonyId + ".beta." + site.ToString().ToLowerInvariant());
                var patches = Harmony.Installed.Where(pair => pair.Value.Owners.Any()).ToArray();
                Check(patches.Length == expected.Count && patches.All(pair =>
                    expected.TryGetValue(pair.Key, out var owner) && pair.Value.Owners.SequenceEqual(new[] { owner })),
                    "unexpected Harmony registration for mask " + mask);

                int before = Recorder.Calls.Count;
                foreach (var site in sites)
                {
                    int i = (int)site;
                    var handle = Submit(site, dep, 200 + i);
                    var call = Recorder.Calls[^1];
                    Check(call.type == (Enabled(site) ? custom : stock)[i] && call.id == 200 + i &&
                        call.dependency.Work == dep.Work && handle.Work == call.work && !handle.IsCompleted,
                        "dispatch " + mask + "/" + site);
                }
                Check(Recorder.Calls.Count == before + sites.Length, "multiple submissions");
                rt.Dispose();
                foreach (var site in sites)
                    Check(Recorder.Calls[before + (int)site].work.Done == Enabled(site), "owned handle");
                Check(Harmony.Installed.Values.All(p => p.Owners.Count == 0), "owner survived unload");
            }
        }
        static void CompanyOwnership()
        {
            var installing = new BetaRuntime(BetaRuntime.GameHash);
            int duringInstall = 0;
            Harmony.BeforePublish = () =>
            {
                foreach (var site in new[] { BetaFeature.CompanyCommercial, BetaFeature.CompanyIndustrial })
                {
                    Submit(site, default, 1);
                    Check(Recorder.Calls[^1].type == stock[(int)site], "company became active before both installs completed");
                    duringInstall++;
                }
            };
            try { installing.Set(BetaFeature.CompanyCommercial, true); }
            finally { Harmony.BeforePublish = null; }
            Check(duringInstall == 4, "both physical installations should publish independently");
            installing.Dispose();
            foreach (var blocked in new[] {BetaFeature.CompanyCommercial, BetaFeature.CompanyIndustrial})
            {
                var other = blocked == BetaFeature.CompanyCommercial ? BetaFeature.CompanyIndustrial : BetaFeature.CompanyCommercial;
                var target = BetaPatches.Transpiler(blocked);
                var foreign = new Harmony("company.foreign");
                foreign.Patch(target, new HarmonyMethod(target));
                var rt = new BetaRuntime(BetaRuntime.GameHash);
                rt.Set(BetaFeature.CompanyCommercial, true);
                void Dispatch(BetaFeature site, bool accelerated)
                {
                    Submit(site, default, (int)site);
                    Check(Recorder.Calls[^1].type == (accelerated ? custom : stock)[(int)site], "company dispatch " + site);
                }
                Dispatch(blocked, false); Dispatch(other, true);
                Check(rt.Status(blocked).StartsWith("Unavailable") && rt.Status(other) == "On", "partial initial availability");
                foreign.UnpatchAll(foreign.Id);
                rt.Set(BetaFeature.CompanyCommercial, true);
                Dispatch(blocked, true);
                BetaPatches.Invalidated = blocked;
                Dispatch(blocked, false); Dispatch(other, true);
                Check(rt.Status(blocked).Contains("schedule call changed") && rt.Status(other) == "On", "changed company site");
                BetaPatches.Invalidated = null;
                foreign.Patch(target, new HarmonyMethod(target));
                Dispatch(blocked, false); Dispatch(other, true);
                Check(rt.Status(blocked).StartsWith("Inactive") && rt.Status(other) == "On", "partial late conflict");
                rt.Set(BetaFeature.CompanyCommercial, false);
                Dispatch(blocked, false); Dispatch(other, false);
                Check(rt.Status(blocked) == "Off" && rt.Status(other) == "Off", "company off");
                rt.Dispose();
                Check(Harmony.GetPatchInfo(target)!.Owners.SequenceEqual(new[]{foreign.Id}), "company removed third-party patch");
                foreign.UnpatchAll(foreign.Id);
            }
        }
        static void LateOwnership()
        {
            var rt = new BetaRuntime(BetaRuntime.GameHash);
            rt.Set(BetaFeature.Water, true); rt.Set(BetaFeature.Route, true);
            var target = BetaPatches.Transpiler(BetaFeature.Water);
            var original = Submit(BetaFeature.Water, default, 1);
            var checkOwner = typeof(BetaRuntime).GetMethod("CheckOwnership", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<BetaFeature,bool,bool,bool>>(rt);
            for (int i=0; i<1000; i++) checkOwner(BetaFeature.Water,false,false);
            int initial = Harmony.RegistryReads;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i=0;i<10000;i++) checkOwner(BetaFeature.Water,false,false);
            Check(GC.GetAllocatedBytesForCurrentThread()==allocated && Harmony.RegistryReads==initial,"steady owner check allocated or polled Harmony");
            for (int i=0; i<4; i++) Submit(BetaFeature.Water, default, i);
            Check(Harmony.RegistryReads==initial, "owner queried on normal submissions");
            var foreign = new Harmony("later");
            var reentered = false;
            Harmony.BeforePublish = () => {
                Submit(BetaFeature.Water, default, 3);
                reentered = Recorder.Calls[^1].type==stock[0];
            };
            foreign.Patch(target, new HarmonyMethod(target)); Harmony.BeforePublish=null;
            Check(reentered && Harmony.RegistryReads==initial,"reentrant unpublished ownership was accepted or cached");
            Submit(BetaFeature.Water, default, 4);
            Check(Recorder.Calls[^1].type==stock[0] && rt.Status(BetaFeature.Water).StartsWith("Inactive"),"late foreign owner active");
            int seen = Harmony.RegistryReads;
            Check(seen==initial+1,"unexpected registry reads after foreign composition");
            for(int i=0;i<4;i++)Submit(BetaFeature.Water,default,5);
            Submit(BetaFeature.Route,default,6);
            Check(Recorder.Calls[^1].type==custom[1] && !original.IsCompleted && Harmony.RegistryReads==seen,"late conflict crossed feature/handle boundaries or reread registry");
            foreign.UnpatchAll(foreign.Id); Submit(BetaFeature.Water,default,7);
            Check(Recorder.Calls[^1].type==custom[0] && Harmony.RegistryReads==seen+1,"foreign removal did not recover");
            BetaPatches.OtherPublisher = BetaFeature.Water; BetaPatches.Recompose(target);
            Submit(BetaFeature.Water,default,7); Check(Recorder.Calls[^1].type==stock[0] && rt.Status(BetaFeature.Water).Contains("Another Harmony assembly"),"uncoordinated publisher accepted");
            BetaPatches.OtherPublisher=null; BetaPatches.Recompose(target);
            foreign.Patch(target,new HarmonyMethod(target)); rt.Set(BetaFeature.Water,false); rt.Set(BetaFeature.Water,true);
            Submit(BetaFeature.Water,default,8); Check(Recorder.Calls[^1].type==stock[0],"re-enable bypassed foreign owner");
            foreign.UnpatchAll(foreign.Id); rt.Set(BetaFeature.Water,true); Submit(BetaFeature.Water,default,9);
            Check(Recorder.Calls[^1].type==custom[0],"explicit retry after conflict did not recover");
            foreign.Patch(target,new HarmonyMethod(target));
            rt.Dispose(); Check(original.IsCompleted && Harmony.GetPatchInfo(target)!.Owners.SequenceEqual(new[]{foreign.Id}),"foreign owner removed or handle abandoned");
            foreign.UnpatchAll(foreign.Id);
        }
        static void DuringPublication()
        {
            var rt = new BetaRuntime(BetaRuntime.GameHash);
            rt.Set(BetaFeature.Route, true);
            using var composing = new System.Threading.ManualResetEventSlim();
            using var publish = new System.Threading.ManualResetEventSlim();
            using var query = new System.Threading.ManualResetEventSlim();
            var foreign = new Harmony("concurrent");
            var target = BetaPatches.Transpiler(BetaFeature.Route);
            var before = Recorder.Calls.Count;
            Harmony.BeforePublish = () => { composing.Set(); if (!publish.Wait(5000)) throw new TimeoutException(); };
            var writer = System.Threading.Tasks.Task.Run(() => foreign.Patch(target, new HarmonyMethod(target)));
            try
            {
                Check(composing.Wait(5000),"foreign composition did not begin");
                Harmony.BeforeLookup = () => query.Set();
                var reader = System.Threading.Tasks.Task.Run(() => Submit(BetaFeature.Route,default,1));
                Check(query.Wait(5000),"dispatcher did not check the pending owner");
                Check(Recorder.Calls.Count==before,"unpublished owner snapshot permitted a submission");
                publish.Set(); System.Threading.Tasks.Task.WaitAll(writer, reader);
                Check(Recorder.Calls.Count==before+1 && Recorder.Calls[^1].type==stock[1],"first post-publication dispatch was not stock");
            }
            finally
            {
                publish.Set(); Harmony.BeforeLookup = Harmony.BeforePublish = null;
                writer.GetAwaiter().GetResult(); rt.Dispose(); foreign.UnpatchAll(foreign.Id);
            }
        }
        static void ForeignPublishingDuringManagement(bool unload)
        {
            var rt = new BetaRuntime(BetaRuntime.GameHash);
            rt.Set(BetaFeature.Water,true); rt.Set(BetaFeature.Route,true);
            using var composing = new System.Threading.ManualResetEventSlim();
            using var inspect = new System.Threading.ManualResetEventSlim();
            var foreign = new Harmony("management-race");
            var target = BetaPatches.Transpiler(BetaFeature.Route);
            JobHandle submitted = default;
            Harmony.BeforePublish = () => {
                Harmony.BeforePublish = null;
                composing.Set(); if (!inspect.Wait(5000)) throw new TimeoutException();
                Check(rt.Status(BetaFeature.Water)=="On","independent status while patching");
                submitted = Submit(BetaFeature.Water,default,11);
            };
            var writer = System.Threading.Tasks.Task.Run(() => foreign.Patch(target,new HarmonyMethod(target)));
            Check(composing.Wait(5000),"foreign writer did not enter its transaction");
            var management = new System.Threading.Thread(() => {
                if (unload) rt.Dispose(); else rt.Set(BetaFeature.CompanyCommercial,true);
            }) { IsBackground=true };
            management.Start();
            // Hold publication until the second thread is known to be waiting.
            // Previously that thread could hold the tracker gate, deadlocking the callback.
            Check(System.Threading.SpinWait.SpinUntil(() => (management.ThreadState & System.Threading.ThreadState.WaitSleepJoin)!=0,5000),"management did not wait for publication");
            inspect.Set();
            Check(writer.Wait(5000) && management.Join(5000),"patch-to-management lock inversion");
            Check(submitted.Work!=null && submitted.IsCompleted==unload,"cross-feature handle during management");
            if (!unload) rt.Dispose();
            Check(Harmony.GetPatchInfo(target)!.Owners.SequenceEqual(new[]{foreign.Id}),"unload disturbed concurrent foreign owner");
            foreign.UnpatchAll(foreign.Id);
        }
        static void Failures()
        {
            BetaRuntime.RejectExistingCoverageOwner(new[]{"foreign"});
            bool duplicate=false;try{BetaRuntime.RejectExistingCoverageOwner(new[]{"foreign",Mod.HarmonyId});}catch(InvalidOperationException){duplicate=true;}
            Check(duplicate,"second assembly may claim existing coverage owner");
            var bad=new BetaRuntime("other build");bad.Set(BetaFeature.Route,true);Submit(BetaFeature.Route,default,0);Check(Recorder.Calls[^1].type==stock[1],"wrong build");bad.Dispose();
            var rt=new BetaRuntime(BetaRuntime.GameHash);var target=BetaPatches.Transpiler(BetaFeature.Water);
            var foreign=new Harmony("foreign");foreign.Patch(target,new HarmonyMethod(target));rt.Set(BetaFeature.Water,true);Submit(BetaFeature.Water,default,1);Check(Recorder.Calls[^1].type==stock[0],"foreign owner");
            rt.Dispose();Check(Harmony.GetPatchInfo(target)!.Owners.SequenceEqual(new[]{"foreign"}),"foreign unpatch");foreign.UnpatchAll(foreign.Id);
            rt=new BetaRuntime(BetaRuntime.GameHash);rt.Set(BetaFeature.Water,true);
            BetaPatches.Invalidated=BetaFeature.Water;
            Submit(BetaFeature.Water,default,0);Check(Recorder.Calls[^1].type==stock[0] && rt.Status(BetaFeature.Water).StartsWith("Inactive"),"invalidated site status");
            BetaPatches.Invalidated=null;
            Check(rt.Status(BetaFeature.Water)=="On","restored site status");
            var first=Submit(BetaFeature.Water,default,1);
            for(int i=1;i<64;i++)Submit(BetaFeature.Water,default,i);Submit(BetaFeature.Water,default,65);
            Check(Recorder.Calls[^1].type==stock[0] && !first.IsCompleted,"busy ring");
            first.Work!.FailCompletion=true;try{rt.Dispose();throw new Exception("failed fence was released");}catch(InvalidOperationException){}
            Submit(BetaFeature.Water,default,7);Check(Recorder.Calls[^1].type==stock[0],"failed drain accepts work");
            first.Work.FailCompletion=false;Harmony.FailUnpatch=true;try{rt.Dispose();throw new Exception("failed unpatch lost");}catch(Exception e)when(e.Message=="unpatch"){}Harmony.FailUnpatch=false;rt.Dispose();
            rt=new BetaRuntime(BetaRuntime.GameHash);rt.Set(BetaFeature.Water,true);int count=Recorder.Calls.Count;
            Recorder.Fail=true;try{Submit(BetaFeature.Water,default,0);throw new Exception("scheduler failure swallowed");}catch(Exception e)when(e.Message=="scheduler"){}Recorder.Fail=false;
            Check(Recorder.Calls.Count==count+1,"ambiguous submission duplicated");rt.Dispose();
            rt = new BetaRuntime(BetaRuntime.GameHash);
            rt.Set(BetaFeature.Route, true);
            var route = Submit(BetaFeature.Route, default, 2);
            rt.Set(BetaFeature.CompanyCommercial, true);
            var commercial = Submit(BetaFeature.CompanyCommercial, default, 20);
            var industrial = Submit(BetaFeature.CompanyIndustrial, default, 21);
            AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("PathfindLocalityFix"), AssemblyBuilderAccess.Run);
            var localSites = new[] { BetaFeature.Route, BetaFeature.CompanyCommercial, BetaFeature.CompanyIndustrial };
            foreach (var site in localSites)
            {
                Submit(site, default, 30);
                Check(Recorder.Calls[^1].type == stock[(int)site] && rt.Status(site).Contains("PathfindLocalityFix"), "late locality conflict: " + site);
            }
            Check(!route.IsCompleted && !commercial.IsCompleted && !industrial.IsCompleted, "early locality drain");
            rt.Dispose();
            Check(route.IsCompleted && commercial.IsCompleted && industrial.IsCompleted, "locality drain");

            rt = new BetaRuntime(BetaRuntime.GameHash);
            rt.Set(BetaFeature.Route, true);
            rt.Set(BetaFeature.CompanyCommercial, true);
            foreach (var site in localSites)
            {
                Submit(site, default, 40);
                Check(Recorder.Calls[^1].type == stock[(int)site] && rt.Status(site).Contains("PathfindLocalityFix"), "existing locality conflict: " + site);
            }
            rt.Dispose();
        }
        static void Manifests()
        {
            var ids=BetaManifest.ReviewedModules; string p=BetaManifest.Prefix;
            var entries=ids.ToDictionary(k=>p+"Module."+k.Key,k=>k.Value.ToString());entries[p+"GameSha256"]=BetaRuntime.GameHash;
            foreach(BetaFeature f in Enum.GetValues<BetaFeature>())entries[p+BetaFeatures.ManifestName(f)]="v1";
            foreach(BetaFeature f in Enum.GetValues<BetaFeature>())BetaManifest.Validate(f,BetaRuntime.GameHash,entries,n=>ids[n]);
            entries.Remove(p + "Company");
            foreach (var site in Enum.GetValues<BetaFeature>())
            {
                bool missing = false;
                try { BetaManifest.Validate(site, BetaRuntime.GameHash, entries, n => ids[n]); }
                catch (InvalidOperationException) { missing = true; }
                Check(missing == BetaFeatures.IsCompany(site), "company manifest affected wrong site: " + site);
            }
            entries[p + "Company"] = "v1";
            foreach(var m in ids.Keys){bool failed=false;try{BetaManifest.Validate(BetaFeature.Route,BetaRuntime.GameHash,entries,n=>n==m?Guid.Empty:ids[n]);}catch(InvalidOperationException){failed=true;}Check(failed,"runtime identity "+m);}
            entries[p+"Module.Unity.Entities"]=Guid.NewGuid().ToString();bool rejected=false;
            try{BetaManifest.Validate(BetaFeature.Water,BetaRuntime.GameHash,entries,n=>Guid.Parse(entries[p+"Module."+n]));}catch(InvalidOperationException){rejected=true;}Check(rejected,"matching unreviewed build/runtime");
        }
        public static void Main(){Manifests();Masks();CompanyOwnership();LateOwnership();DuringPublication();ForeignPublishingDuringManagement(false);ForeignPublishingDuringManagement(true);Failures();Console.WriteLine("PASS 8 toggle combinations with exact registration and dispatch across all four active sites, partial company ownership, original inputs and handles, ten ABI mismatch controls, unsupported build, concurrent foreign publication/toggle/unload, ring saturation, scheduler exception, retryable drain/unpatch and late route and company conflicts");}
    }
}
