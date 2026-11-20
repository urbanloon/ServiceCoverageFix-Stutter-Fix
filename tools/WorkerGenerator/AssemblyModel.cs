using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class AssemblyModel
{
    private readonly CSharpDecompiler _decompiler;

    internal AssemblyModel(string path)
    {
        var resolver = new UniversalAssemblyResolver(path, false, ".NETFramework,Version=v4.8");
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
        _decompiler = new CSharpDecompiler(path, resolver, new DecompilerSettings(LanguageVersion.CSharp9_0));
    }

    internal string DecompileType(string name) => _decompiler.DecompileTypeAsString(new FullTypeName(name));
}
