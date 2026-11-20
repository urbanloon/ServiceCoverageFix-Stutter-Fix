"""Build the existing game/mod visit tracer with only its input source replaced."""
import argparse, pathlib, shutil, subprocess, os
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('repo',type=pathlib.Path); p.add_argument('scratch',type=pathlib.Path); p.add_argument('managed',type=pathlib.Path); p.add_argument('mod',type=pathlib.Path); p.add_argument('seed',type=int); p.add_argument('--shapes',default='local,citywide,ties,tight'); args=p.parse_args()
shapes=args.shapes.split(',')
if not shapes or not set(shapes) <= {'local','citywide','ties','tight','mixed'}: p.error('invalid shapes')
repo=args.repo.resolve(); out=args.scratch.resolve(); out.mkdir(parents=True,exist_ok=True)
fixture=(repo/'tests/park_overlap/Program.cs').read_text()
start=fixture.index('    // rounds seed'); fixture=fixture[:start]+'}\n'
fixture=fixture.replace('internal static class Program','internal static class ParkFixture').replace('private static Inputs Make(', 'internal static Inputs Make(')
(out/'ParkFixture.cs').write_text(fixture)
for name in ['Instrument.cs','Program.cs','VisitOrder.csproj']: shutil.copy(repo/'tests/visit_order'/name, out/name)
s=(out/'Program.cs').read_text()
point='int equal=0;'
s=s.replace(point,'''var visitedRoads=baseline.Where(e=>!e.Store).Select(e=>(int)input.Elements[e.Index].Target).Distinct().Count();
                var storedRoads=baseline.Where(e=>e.Store).Select(e=>e.Index).Distinct().Count();
                Console.WriteLine($"STOCK_ROADS,{label},{visitedRoads},{storedRoads}");
                '''+point)
(out/'Program.cs').write_text(s)
(out/'AllCases.cs').write_text('''internal static class AllCases
{
    internal static IEnumerable<(string Name,Inputs Input)> Create()
    {
        foreach(string shape in new[]{SHAPES})
            yield return(shape, ParkFixture.Make(SEED,shape));
    }
}
'''.replace('SEED',str(args.seed)).replace('SHAPES',','.join(chr(34)+s+chr(34) for s in shapes)))
csproj=out/'VisitOrder.csproj'; csproj.write_text(csproj.read_text().replace('../game_compare',str(repo/'tests/game_compare')))
subprocess.run(['dotnet','build',csproj,'-c','Release','-p:UseSharedCompilation=false','-p:GameManagedPath='+str(args.managed.resolve())],check=True)
subprocess.run(['dotnet',out/'bin/Release/net8.0/VisitOrder.dll',args.managed.resolve(),args.mod.resolve()],check=True)
