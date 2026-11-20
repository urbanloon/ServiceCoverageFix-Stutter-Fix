internal static class FrontierCases
{
    // Enumerate every placement of NaN, equal and unequal keys in three two-item
    // streams. Shared targets make both visit order and float writes observable.
    internal static IEnumerable<(string, Inputs)> Create()
    {
        float[] symbols = { -1f, +1f, BitConverter.UInt32BitsToSingle(0x7fc00009) };
        for (int code = 0; code < 729; code++)
        {
            int value = code;
            var input = Inputs.Make(1103, 3, 2, 2, true);
            for (int i = 0; i < input.Elements.Length; i++)
            {
                input.Elements[i].Priority = symbols[value % symbols.Length];
                value /= symbols.Length;
            }
            yield return ("all-three-symbol-placements", input);
        }
        foreach (int count in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 255, 256, 257, 525, 526, 527, 1023, 1024, 1025, 1052 })
        {
            foreach (int shape in Enumerable.Range(0, 5))
            {
                const int length = 19;
                var input = Inputs.Make(2511, count, length, 2, true);
                for (int i = 0; i < count; i++)
                {
                    if (shape == 4 && i % 53 != 1) input.Providers[i].Count = 0;
                    for (int j = 0; j < length; j++)
                    {
                        uint bits = shape switch {
                            0 => (uint)(0x3f800000 - j * 32 - i % 31),
                            1 => (uint)(0x3f800000 - j / 3),
                            2 => (uint)(0x3f800000 - j * (i % 6 + 1)),
                            3 => (uint)(i % 2 == 0 ? 0x007fffff - j : 0x00800000 - j),
                            _ => (uint)(0x3f800000 - j - i % 11)
                        };
                        input.Elements[i * length + j].Priority = BitConverter.UInt32BitsToSingle(bits);
                    }
                }
                yield return ("provider-boundaries-" + shape, input);
            }
        }
    }
}
