using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

if (args.Length < 2)
{
    Console.Error.WriteLine("GameCompare <game Managed directory> <candidate DLL> [baseline DLL] [--timing] [--require-nan]");
    return 2;
}
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var baselinePath = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null;
var jobs = new Jobs(args[0], args[1], baselinePath);
int compared = 0, nanMismatches = 0;
long inputs = 0;
Console.WriteLine("Game SHA256 " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(args[0],"Game.dll")))));
for (int mode = 0; mode < 8; mode++)
{
    int modesCompared = 0;
    for (int seed = 0; seed < 192; seed++)
    {
        int size = new[] { 0, 1, 2, 3, 4, 8, 16, 17, 20, 49, 76, 127 }[seed % 12];
        var input = Inputs.Make(seed, size, 31, mode);
        using var pass = new Pass(input);
        pass.Execute(jobs.Vanilla);
        pass.Check();
        var expected = pass.Outputs();
        pass.Reset();
        pass.Execute(jobs.Candidate);
        pass.Check();
        if (!pass.Outputs().AsSpan().SequenceEqual(expected))
        {
            if (mode < 5 || args.Contains("--require-nan")) throw new Exception($"Mismatch in mode {mode}, seed {seed}");
            nanMismatches++;
        }
        else modesCompared++;
        inputs += input.Elements.Length;
    }
    compared += modesCompared;
    Console.WriteLine($"Mode {mode}: {modesCompared}/192 bitwise matches.");
}
Console.WriteLine($"Bitwise matches={compared}, elements={inputs}, NaN-probe differences={nanMismatches}");
if (args.Contains("--require-nan")) UnorderedPriorities.Verify(jobs);
foreach (var count in new[] { 8193, 65537 })
{
    var input = Inputs.Make(173, count, 1, 1, true);
    using var pass = new Pass(input);
    pass.Execute(jobs.Vanilla);
    pass.Check();
    var expected = pass.Outputs();
    pass.Reset();
    pass.Execute(jobs.Candidate);
    pass.Check();
    if (!pass.Outputs().AsSpan().SequenceEqual(expected))
        throw new Exception($"Mismatch with {count} active providers");
    Console.WriteLine($"PASS: {count} active providers, one tied-priority element each.");
}
if (!args.Contains("--timing")) return 0;
if (jobs.Baseline == null) throw new ArgumentException("Timings need a separate baseline mod DLL.");
foreach (var (name, providers, length, mode, repetitions) in new[] {
    ("Single",1,32,0,3000),("Two providers",2,32,0,1500),("Four providers",4,32,0,1000),
    ("Light",12,64,0,200),("Mixed",49,128,0,30),
    ("Park scale, interleaved",526,970,0,1),("Park scale, dense ties",526,970,1,1),
    ("Twice the park providers",1052,970,0,1) })
{
    var input = Inputs.Make(351,providers,length,mode,true);
    using var pass = new Pass(input);
    pass.Execute(jobs.Vanilla); pass.Check();
    var expected = pass.Outputs();
    foreach (var job in new[] { jobs.Candidate,jobs.Baseline })
    {
        pass.Reset(); pass.Execute(job); pass.Check();
        if(!pass.Outputs().AsSpan().SequenceEqual(expected)) throw new Exception("Large comparison failed: "+name);
    }
    // Prepare independent input states outside the timed regions.
    using var cohort = new PassGroup(input, repetitions);
    for (int i=0;i<6;i++) cohort.Timed(i%2==0?jobs.Baseline:jobs.Candidate);
    var before = new List<double>();
    var after = new List<double>();
    var ratio = new List<double>();
    for (int round=0;round<21;round++)
    {
        double first, second;
        if(round%2==0) { first=cohort.Timed(jobs.Baseline); second=cohort.Timed(jobs.Candidate); }
        else { second=cohort.Timed(jobs.Candidate); first=cohort.Timed(jobs.Baseline); }
        before.Add(first); after.Add(second); ratio.Add(first/second);
    }
    before.Sort(); after.Sort(); ratio.Sort();
    Console.WriteLine($"TIMING,{name},{providers},{providers*length},{before[10]:F5},{after[10]:F5},{ratio[10]:F4},{ratio[2]:F4},{ratio[18]:F4}");
}
return 0;

internal sealed class PassGroup : IDisposable
{
    private readonly Pass[] passes;
    internal PassGroup(Inputs input, int count) { passes=Enumerable.Range(0,count).Select(_=>new Pass(input)).ToArray(); }
    internal double Timed(Action<nint,nint> job)
    {
        foreach (var pass in passes) pass.Reset();
        long start = Stopwatch.GetTimestamp();
        foreach (var pass in passes) pass.Execute(job);
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds / passes.Length;
    }
    public void Dispose() { foreach(var pass in passes) pass.Dispose(); }
}
