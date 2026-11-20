using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ServiceCoverageFix
{
    internal static class BetaManifest
    {
        internal const string Prefix = "ServiceCoverageFix.Beta.";
        internal static readonly IReadOnlyDictionary<string,Guid> ReviewedModules = new Dictionary<string,Guid>
        {
            { "Game", new Guid("46198354-d4f9-4d23-bf32-7e3c085d8b92") },
            { "Colossal.Collections", new Guid("1817618d-5b2d-40eb-9904-1021cd3b6e28") },
            { "Colossal.Core", new Guid("4e1271d0-ee7e-4fff-afd8-c460d5ad5c2d") },
            { "Colossal.Mathematics", new Guid("47506b75-6d9c-4ea7-a027-823f03022cb2") },
            { "Unity.Burst", new Guid("45d4a897-bd4c-424a-8585-7471450c092f") },
            { "Unity.Burst.Unsafe", new Guid("9eaa2e25-81d1-45ae-9c55-5d2492ea3922") },
            { "Unity.Collections", new Guid("4951ab81-44ac-480a-9dbc-bef7fda2a8c4") },
            { "Unity.Entities", new Guid("bfb47e8c-9b6e-4acf-8c6d-c80a7bff1c41") },
            { "Unity.Mathematics", new Guid("25bcc716-037f-4638-8e72-c0b71c07ade8") },
            { "UnityEngine.CoreModule", new Guid("3f4ee31e-4ddf-4e8a-936e-abcff96e3617") },
        };
        internal static void Validate(BetaFeature feature, string gameHash)
        {
            var entries = typeof(BetaManifest).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(a => a.Key.StartsWith(Prefix, StringComparison.Ordinal)).ToDictionary(a => a.Key, a => a.Value ?? string.Empty);
            Validate(feature, gameHash, entries, name =>
            {
                var resolved = Assembly.Load(new AssemblyName(name));
                if (AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == name) != 1)
                    throw new InvalidOperationException("Ambiguous assembly " + name);
                return resolved.ManifestModule.ModuleVersionId;
            });
        }
        internal static void Validate(BetaFeature feature, string gameHash, IDictionary<string,string> entries, Func<string,Guid> actualId)
        {
            if (gameHash != BetaRuntime.GameHash || !entries.TryGetValue(Prefix + "GameSha256", out var recorded) || recorded != gameHash ||
                !entries.TryGetValue(Prefix + feature, out var version) || version != "v1")
                throw new InvalidOperationException("Worker generation stamp or game fingerprint mismatch.");
            const string modules = Prefix + "Module.";
            if (entries.Keys.Count(key => key.StartsWith(modules, StringComparison.Ordinal)) != ReviewedModules.Count)
                throw new InvalidOperationException("Incomplete worker assembly manifest.");
            foreach (var reviewed in ReviewedModules)
            {
                var name = reviewed.Key;
                if (!entries.TryGetValue(modules + name, out var id) || !Guid.TryParse(id, out var expected) ||
                    expected != reviewed.Value || actualId(name) != expected)
                    throw new InvalidOperationException("Worker assembly mismatch: " + name);
            }
        }
    }
}
