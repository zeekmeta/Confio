using System.Text.Json.Nodes;

namespace Confio.Internal;

internal sealed class NodeSlot
{
    private readonly JsonObject _parent;
    private readonly string _property;

    internal NodeSlot(JsonObject parent, string property, string pointer, string protectionPointer)
    {
        _parent = parent;
        _property = property;
        Pointer = pointer;
        ProtectionPointer = protectionPointer;
    }

    internal string Pointer { get; }
    internal string ProtectionPointer { get; }
    internal JsonNode? Value => _parent[_property];

    internal void Set(JsonNode? value) => _parent[_property] = value;
}
