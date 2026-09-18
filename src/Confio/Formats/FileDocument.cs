using System.Collections.Generic;
using System.Text.Json.Nodes;
using Confio.Internal;

namespace Confio.Formats;

// 文件文档只在一次操作内使用；模型读取投影不能反向覆盖其他配置节的原生数据。
internal abstract class FileDocument
{
    internal abstract JsonObject ReadNodes(IEnumerable<SettingsDeclaration> declarations, SettingsDeclaration? excluded = null);
    internal abstract void Set(string[] segments, JsonNode value, ModelSchema? schema = null);
    internal abstract bool Remove(string[] segments);
    internal abstract byte[] Encode();

    internal void ApplyProtection(IEnumerable<NodeSlot> slots)
    {
        foreach (var slot in slots)
        {
            Set(ConfigurationPath.FromPointer(slot.Pointer), slot.Value!.DeepClone());
        }
    }
}
