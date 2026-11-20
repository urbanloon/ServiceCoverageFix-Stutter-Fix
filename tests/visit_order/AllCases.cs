internal static class AllCases
{
    internal static IEnumerable<(string Name, Inputs Input)> Create()
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
        foreach (int providers in new[] { 1, 2, 4, 5, 8, 15, 16, 17 })
        {
            for (int seed = 0; seed < 8; seed++)
            {
                foreach (string shape in new[] { "ties", "subnormal", "initial-nan", "interior-nan", "filter" })
                {
                    int extra = shape == "filter" ? 3 : 0;
                    var input = Inputs.Make(seed, providers + extra, 32, 1, true);
                    for (int i = 0; i < input.Providers.Length; i++)
                    {
                        var provider = input.Providers[i];
                        // All requested providers start live, including some with
                        // a short budget. Filter three others in swap-back order.
                        provider.Total = provider.Remaining = (i + seed) % 3 == 0 ? .2f : 1000f;
                        if (extra != 0 && i < extra) provider.Remaining = 0;
                        input.Providers[i] = provider;
                        for (int j = 0; j < provider.Count; j++)
                        {
                            var element = input.Elements[provider.Start + j];
                            element.Target %= 4;
                            float priority = (31 - j) / 4;
                            if (shape == "subnormal")
                                priority = (15 - j + (i % 3)) * float.Epsilon;
                            if ((shape == "initial-nan" && j == 0 && (i + seed) % 3 == 0) ||
                                (shape == "interior-nan" && j > 0 && (j + i) % 5 == 0))
                                priority = BitConverter.UInt32BitsToSingle(0x7fc00003u);
                            element.Priority = priority;
                            input.Elements[provider.Start + j] = element;
                        }
                    }
                    yield return ($"boundary-{providers}-{shape}", input);
                }
            }
        }
        foreach(var group in FrontierCases.Create()) yield return group;
        foreach (var pair in new[]{("heavy-distinct",0),("heavy-ties",1),("heavy-shared-target",2)})
            yield return (pair.Item1,Inputs.Make(351,526,970,pair.Item2,true));
    }
}
