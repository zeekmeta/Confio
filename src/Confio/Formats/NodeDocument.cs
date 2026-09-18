using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Confio.Internal;

namespace Confio.Formats;

internal class NodeDocument(JsonObject document, Func<JsonObject, byte[]> encode) : FileDocument
{
    protected JsonObject Document { get; } = document;

    internal override JsonObject ReadNodes(IEnumerable<SettingsDeclaration> declarations, SettingsDeclaration? excluded = null) =>
        (JsonObject)Document.DeepClone();

    internal override void Set(string[] segments, JsonNode value, ModelSchema? schema = null) =>
        ConfigurationPath.Set(Document, segments, value);

    internal override bool Remove(string[] segments) => ConfigurationPath.Remove(Document, segments);
    internal override byte[] Encode() => encode(Document);
}
