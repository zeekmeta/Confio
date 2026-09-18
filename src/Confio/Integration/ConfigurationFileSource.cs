using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Confio;

internal sealed class ConfigurationFileSource(ConfigurationFile file, bool ownsFile) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(file, ownsFile);

    private sealed class Provider(ConfigurationFile file, bool ownsFile) : IConfigurationProvider, IDisposable
    {
        private bool _loaded;

        public bool TryGet(string key, out string? value) => file.ConfigurationValues.TryGetValue(key, out value);

        public void Set(string key, string? value) =>
            throw new NotSupportedException("This configuration provider is read-only. Use ConfigurationFile or ISettings<T> to save changes.");

        public void Load()
        {
            // 启动入口已预加载；后续原生重载复用同一文件加载和保护路径。
            if (_loaded) file.Reload();
            _loaded = true;
        }

        public void Dispose()
        {
            if (ownsFile) file.Dispose();
        }

        public IChangeToken GetReloadToken() => file.GetReloadToken();

        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
        {
            var prefix = parentPath is null ? "" : parentPath + ":";
            return file.ConfigurationValues.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(key => key.Substring(prefix.Length).Split(new[] { ':' }, 2)[0])
                .Concat(earlierKeys)
                .OrderBy(key => key, ConfigurationKeyComparer.Instance);
        }
    }
}
