using Colossal.Mono.Cecil;

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(args[0]);
using var definition = ModuleDefinition.ReadModule(Path.Combine(args[0],"Unity.Entities.dll"),
    new ReaderParameters { AssemblyResolver = resolver });
foreach (var type in definition.GetTypes())
{
    if (type.IsNested) type.IsNestedPublic = true;
    else if (type.Name != "<Module>") type.IsPublic = true;
    foreach (var field in type.Fields)
    {
        field.IsPublic = true;
        field.IsInitOnly = false;
    }
    foreach (var method in type.Methods) method.IsPublic = true;
}
definition.Write(args[1]);
