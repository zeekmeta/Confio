using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Confio.Internal;

namespace Confio.Protection;

internal sealed class NodeProtection
{
    private const string Prefix = "enc:v1:";
    private readonly IConfigurationProtector _protector;

    internal NodeProtection(IConfigurationProtector protector)
    {
        _protector = protector;
    }

    internal static bool IsEncrypted(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) &&
        text.StartsWith("enc:", StringComparison.Ordinal);

    internal void Protect(IEnumerable<NodeSlot> slots)
    {
        foreach (var slot in slots)
        {
            var plaintext = JsonData.Encode(slot.Value!);
            try
            {
                slot.Set(Envelope(_protector.Protect(plaintext, Purpose(slot.ProtectionPointer))));
            }
            finally
            {
                plaintext.AsSpan().Clear();
            }
        }
    }

    internal async Task ProtectAsync(IEnumerable<NodeSlot> slots, CancellationToken cancellationToken)
    {
        foreach (var slot in slots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plaintext = JsonData.Encode(slot.Value!);
            try
            {
                var ciphertext = await _protector.ProtectAsync(plaintext, Purpose(slot.ProtectionPointer), cancellationToken).ConfigureAwait(false);
                slot.Set(Envelope(ciphertext));
            }
            finally
            {
                plaintext.AsSpan().Clear();
            }
        }
    }

    internal void Unprotect(IEnumerable<NodeSlot> slots)
    {
        foreach (var slot in slots)
        {
            var plaintext = _protector.Unprotect(Payload(slot.Value!), Purpose(slot.ProtectionPointer));
            try
            {
                slot.Set(ParsePayload(plaintext));
            }
            finally
            {
                plaintext.AsSpan().Clear();
            }
        }
    }

    internal async Task UnprotectAsync(IEnumerable<NodeSlot> slots, CancellationToken cancellationToken)
    {
        foreach (var slot in slots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plaintext = await _protector.UnprotectAsync(
                Payload(slot.Value!), Purpose(slot.ProtectionPointer), cancellationToken).ConfigureAwait(false);
            try
            {
                slot.Set(ParsePayload(plaintext));
            }
            finally
            {
                plaintext.AsSpan().Clear();
            }
        }
    }

    private static string Purpose(string pointer) => "Confio/v1\0" + pointer;

    private static JsonValue Envelope(byte[] ciphertext)
    {
        if (ciphertext is null || ciphertext.Length == 0)
        {
            throw new InvalidDataException("The protection provider returned an empty payload.");
        }
        return JsonValue.Create(Prefix + Convert.ToBase64String(ciphertext));
    }

    private static byte[] Payload(JsonNode node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) ||
            !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The protected payload has an unsupported version or invalid shape.");
        }
        try
        {
            var bytes = Convert.FromBase64String(text[Prefix.Length..]);
            return bytes.Length != 0 ? bytes : throw new InvalidDataException("The protected payload is empty.");
        }
        catch (FormatException)
        {
            throw new InvalidDataException("The protected payload has invalid encoding.");
        }
    }

    private static JsonNode? ParsePayload(byte[] plaintext)
    {
        try
        {
            var node = JsonNode.Parse(plaintext, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                new JsonDocumentOptions { MaxDepth = JsonData.MaximumDepth });
            JsonData.Validate(node);
            return node;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException("The authenticated payload does not contain a valid configuration value.");
        }
    }
}
