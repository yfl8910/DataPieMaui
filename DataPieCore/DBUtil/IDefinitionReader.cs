using System.Collections.Generic;

namespace DBUtil;

public sealed record DefinitionObject(string Kind, string Schema, string Name, int Id = 0)
{
    public string DisplayName => string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name;
}
public sealed record ObjectDefinition(string Sql, string Notice = "");

// List calls never read SQL bodies. Single-object definitions are read only on request.
public interface IDefinitionReader
{
    IReadOnlyList<DefinitionObject> ListDefinitionObjects();
    ObjectDefinition ReadDefinition(DefinitionObject item);
}
