internal static class UnorderedPriorities
{
    // Each payload is supplied as priority data independent of target arithmetic,
    // so a change to provider selection remains observable in finite target outputs.
    private static readonly uint[] NaNs =
    {
        0x7F800001u, 0x7FC00003u, 0x7FFFFFFFu,
        0xFF800001u, 0xFFC00003u, 0xFFFFFFFFu
    };
    private static readonly float[] Numeric =
    {
        float.PositiveInfinity, float.NegativeInfinity, float.Epsilon, -float.Epsilon,
        -0.0f, 0.0f, float.MaxValue, -float.MaxValue
    };

    internal static void Verify(Jobs jobs)
    {
        int[] sizes = { 0, 1, 2, 3, 4, 8, 16, 17, 20, 49, 76, 127 };
        int workloads = 0;
        long elements = 0;
        for (int mode = 0; mode < 8; mode++)
        {
            for (int seed = 0; seed < 192; seed++)
            {
                var input = Inputs.Make(seed, sizes[seed % sizes.Length], 31, 0);
                var rng = new Random(seed);
                for (int i = 0; i < input.Providers.Length; i++)
                {
                    var provider = input.Providers[i];
                    for (int j = 0; j < provider.Count; j++)
                    {
                        bool nan = mode switch
                        {
                            0 => false,                          // Deliberately unsorted numeric streams
                            1 => j > 0 && j % 8 < 4,             // NaN runs after an ordered head
                            2 => j == 0 && i % 4 == 0,           // Multiple initially unordered heads
                            3 => true,
                            4 => j == provider.Count - 1,
                            5 => j == 1 || j == 2 || j == 4,
                            6 => i % 2 == 0 && j % 3 == 0,
                            _ => (i+j) % 7 == 2
                        };
                        int index = i + j + seed;
                        int choice = index % 17;
                        input.Elements[provider.Start + j].Priority = nan
                            ? BitConverter.UInt32BitsToSingle(NaNs[index % NaNs.Length])
                            : choice < Numeric.Length ? Numeric[choice] : rng.Next(-16,16) / 4f;
                    }
                }
                using var pass = new Pass(input);
                pass.Execute(jobs.Vanilla);
                pass.Check();
                byte[] expected = pass.Outputs();
                pass.Reset();
                pass.Execute(jobs.Candidate);
                pass.Check();
                if (!pass.Outputs().AsSpan().SequenceEqual(expected))
                    throw new Exception($"Unordered priority mismatch in mode {mode}, seed {seed}");
                workloads++;
                elements += input.Elements.Length;
            }
        }
        Console.WriteLine($"PASS: {workloads} unordered-priority workloads, {elements} elements, all target bits matched.");
    }
}
