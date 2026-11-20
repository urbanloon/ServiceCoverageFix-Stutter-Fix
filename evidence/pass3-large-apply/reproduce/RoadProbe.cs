internal static class RoadProbe
{
    private const int RoadCount = 6128;
    private const int Providers = 526;
    private const int VisitsPerProvider = 2114;

    // Counts come from a saved city and a separate live observation. The
    // specific road lists, road layout, budgets and priorities are synthetic.
    internal static Inputs Make(int seed, string shape)
    {
        var rng = new Random(seed);
        var providers = new Provider[Providers];
        var elements = new Element[Providers * VisitsPerProvider];
        const int slots = 9;
        const int park = 3;
        var targets = new float[2 * slots * RoadCount];
        Array.Fill(targets, -0.321f); // Adjacent slots must survive unchanged.
        for (int r = 0; r < RoadCount; ++r)
            Array.Clear(targets, 2 * (slots * r + park), 2);

        int window = shape == "local" ? 2560 : RoadCount;
        var choices = new int[window];
        for (int i = 0; i < Providers; ++i)
        {
            int start = i * VisitsPerProvider;
            // The final scenario forces retirement, even in a crowded city.
            float budget = shape == "tight" ? 0.25f + rng.NextSingle() : 200f + rng.NextSingle() * 800f;
            providers[i] = new Provider { Entity = i, Start = start, Count = VisitsPerProvider,
                Total = budget, Remaining = budget };
            int anchor = rng.Next(RoadCount);
            for (int j = 0; j < window; ++j) choices[j] = j;
            rng.Shuffle(choices.AsSpan());
            for (int j = 0; j < VisitsPerProvider; ++j)
            {
                int road = (anchor + choices[j]) % RoadCount;
                float value = shape == "ties" ? rng.Next(1, 17) : 0.05f + 20f * rng.NextSingle();
                float skew = shape == "ties" ? 0f : 0.04f * rng.NextSingle();
                float x = value + skew, y = value - skew;
                elements[start + j] = new Element { Target = road * slots + park,
                    X = x, Y = y, Priority = (x + y) * 0.5f,
                    Density = j % 3 == 0 ? 1f : 0.1f, Length = 1f + 16f * rng.NextSingle() };
            }
            Array.Sort(elements, start, VisitsPerProvider,
                Comparer<Element>.Create((a,b) => b.Priority.CompareTo(a.Priority)));
        }
        return new Inputs(providers, elements, targets);
    }

}
