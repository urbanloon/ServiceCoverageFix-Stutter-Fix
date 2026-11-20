internal static class AllCases
{
    private static readonly int[] Sizes = {1, 2, 15, 16, 17, 18, 33, 65, 257, 525, 533, 1154};
    private static readonly float[] Special = {
        float.NegativeInfinity, -1f, -0f, 0f, float.Epsilon, -float.Epsilon, 1f, float.PositiveInfinity,
        BitConverter.UInt32BitsToSingle(0x7fc01234), BitConverter.UInt32BitsToSingle(0xffc00001),
        BitConverter.UInt32BitsToSingle(0x7f800001)
    };

    internal static IEnumerable<(string Name, Inputs Input)> Create()
    {
        foreach (var size in Sizes)
        {
            for (int seed = 0; seed < 33; seed++)
            {
                var rng = new Random(0x761003 + 99 * size + seed);
                var input = Inputs.Make(rng.Next(), size, 13, 0, true);
                for (int i = 0; i < size; i++)
                {
                    var p = input.Providers[i];
                    if (i % 7 == 1) p.Start -= rng.Next(1, 4);
                    p.Count = rng.Next(14);
                    p.Total = seed % 3 == 0 ? Special[rng.Next(Special.Length)] : rng.Next(11);
                    p.Remaining = i % 5 == 0 ? Special[rng.Next(Special.Length)] : rng.NextSingle() * 4f;
                    input.Providers[i] = p;
                }
                for (int i = 0; i < input.Elements.Length; i++)
                {
                    var e = input.Elements[i];
                    e.Target = rng.Next(3);
                    e.Priority = (seed % 3) switch
                    {
                        0 => Special[rng.Next(Special.Length)],
                        1 => BitConverter.UInt32BitsToSingle((uint)rng.NextInt64(0L, 1L << 32)),
                        _ => (float)(13 - i % 13) / 4
                    };
                    if (seed % 4 == 0)
                    {
                        e.Density = Special[rng.Next(Special.Length)];
                        e.Length = Special[rng.Next(Special.Length)];
                        e.X = Special[rng.Next(Special.Length)];
                        e.Y = rng.NextSingle() * 2f;
                    }
                    input.Elements[i] = e;
                }
                for (int i = 0; i < 6; i++) input.InitialTargets[i] = seed % 2 == 0 ?
                    Special[rng.Next(Special.Length)] : rng.NextSingle() * 0.4f;
                yield return ($"alias-edge-{size}", input);
            }
        }
    }
}
