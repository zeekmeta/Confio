using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Confio;

internal sealed class SettingsOptionsFactory<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
    ConfigurationFile file, Func<T> createDefault,
    IEnumerable<IConfigureOptions<T>> configure,
    IEnumerable<IPostConfigureOptions<T>> postConfigure,
    IEnumerable<IValidateOptions<T>> validate)
    : OptionsFactory<T>(configure, postConfigure, validate) where T : class
{
    protected override T CreateInstance(string name) =>
        name == Options.DefaultName ? file.ReadLoaded<T>() : createDefault();
}

internal sealed class SettingsChangeTokenSource<T>(ConfigurationFile file) : IOptionsChangeTokenSource<T> where T : class
{
    public string Name => Options.DefaultName;
    public IChangeToken GetChangeToken() => file.GetReloadToken();
}
