using System.Security.Cryptography;

public static class Probe
{
    internal readonly record struct Entry(bool Store, int Index, long Bits);
    internal static List<Entry> Events = new();
    static nint elements, targets;
    static int elementCount, targetCount;
    internal static void Begin(nint elementBase, nint targetBase, int n, int m)
    {
        elements = elementBase; targets = targetBase; elementCount = n; targetCount = m;
        Events = new List<Entry>(n);
    }
    public static void VisitIndex(int index)
    {
        if ((uint)index >= (uint)elementCount) throw new Exception("Invalid selected index");
        Events.Add(new Entry(false,index,0));
    }
    public static void VisitAddress(nint pointer)
    {
        long delta = pointer-elements;
        if (delta % 32 != 0) throw new Exception("Unaligned element");
        VisitIndex(checked((int)(delta/32)));
    }
    public static void Store(nint pointer, long bits)
    {
        long delta = pointer-targets;
        if (delta % 8 != 0 || delta < 0 || delta / 8 >= targetCount) throw new Exception("Invalid write address");
        Events.Add(new Entry(true,(int)(delta/8),bits));
    }
}

unsafe class Program
{
    static IEnumerable<(string Name, Inputs Input)> Cases()
    {
        int[] sizes = {0,1,2,3,4,8,16,17,20,49,76,127};
        for (int mode=0; mode<8; mode++)
            for (int seed=0; seed<192; seed++)
                yield return ("mode-"+mode,Inputs.Make(seed,sizes[seed % sizes.Length],31,mode));
        foreach(int providers in new[]{1,2,3,4,17,128})
        {
            var occluded=Inputs.Make(991,providers,49,1,true);
            Array.Fill(occluded.InitialTargets,2f);
            yield return ("fully-occluded",occluded);
            var overlapping=Inputs.Make(117,providers,49,1,true);
            for(int i=0; i<overlapping.Providers.Length; i++) overlapping.Providers[i].Start=0;
            yield return ("overlapping-slices",overlapping);
        }
        foreach (var pair in new[]{("heavy-distinct",0),("heavy-ties",1),("heavy-shared-target",2)})
            yield return (pair.Item1,Inputs.Make(351,526,970,pair.Item2,true));
    }
    static List<Probe.Entry> Observe(Pass pass, Inputs input, Action<nint,nint> job)
    {
        pass.Execute((providers,list)=>{
            nint rows=*(nint*)list;
            nint targets=input.Elements.Length==0 ? 0 : *(nint*)rows-input.Elements[0].Target*8;
            Probe.Begin(rows,targets,input.Elements.Length,input.InitialTargets.Length/2);
            job(providers,list);
        });
        pass.Check();
        return Probe.Events;
    }
    static void Main(string[] args)
    {
        string managed=Path.GetFullPath(args[0]), candidate=Path.GetFullPath(args[1]);
        string temp=Directory.CreateTempSubdirectory("coverage-order-").FullName;
        try
        {
            string game=Path.Combine(managed,"Game.dll");
            foreach (string f in new[]{game,candidate})
                Console.WriteLine($"{Path.GetFileName(f)} original SHA-256 {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant()}");
            foreach(var dependency in Directory.EnumerateFiles(managed,"*.dll"))
                if (Path.GetFileName(dependency)!="Game.dll") File.CreateSymbolicLink(Path.Combine(temp,Path.GetFileName(dependency)),dependency);
            Instrument.Copy(game,Path.Combine(temp,"Game.dll"),true,managed);
            var modified=Path.Combine(temp,"Candidate.dll");
            Instrument.Copy(candidate,modified,false,managed);
            var jobs=new Jobs(temp,modified,null);
            var totals=new Dictionary<string,(int Cases,long Visits,long Writes)>();
            foreach(var (label,input) in Cases())
            {
                using var pass=new Pass(input);
                var baseline=Observe(pass,input,jobs.Vanilla);
                byte[] output=pass.Outputs();
                pass.Reset();
                var result=Observe(pass,input,jobs.Candidate);
                int equal=0;
                while(equal<baseline.Count && equal<result.Count && baseline[equal]==result[equal]) equal++;
                if(equal!=baseline.Count || equal!=result.Count)
                    throw new Exception($"{label} mismatch at event {equal}: vanilla {baseline.ElementAtOrDefault(equal)} vs candidate {result.ElementAtOrDefault(equal)}");
                if(!pass.Outputs().AsSpan().SequenceEqual(output)) throw new Exception("Final output mismatch");
                int writes=baseline.Count(e=>e.Store);
                var t=totals.GetValueOrDefault(label);
                totals[label]=(t.Cases+1,t.Visits+baseline.Count-writes,t.Writes+writes);
            }
            Console.WriteLine("group,cases,selected_elements,target_stores");
            foreach(var (name,t) in totals) Console.WriteLine($"{name},{t.Cases},{t.Visits},{t.Writes}");
            Console.WriteLine($"PASS {totals.Values.Sum(t=>t.Cases)} cases; {totals.Values.Sum(t=>t.Visits)} visits; {totals.Values.Sum(t=>t.Writes)} stores. Interleaved visits, destination IDs, both float bit patterns, final bytes and guards match.");
        }
        finally { Directory.Delete(temp,true); }
    }
}
