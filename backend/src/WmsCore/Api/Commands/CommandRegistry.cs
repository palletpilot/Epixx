using System.Text.Json;

namespace Lagerkraft.WmsCore.Api.Commands;

public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommandHandler> _handlers = new(StringComparer.Ordinal);
    private readonly List<IUpcaster> _upcasters = [];

    public void Register(ICommandHandler handler) => _handlers[handler.Type] = handler;

    public void Register(IUpcaster upcaster) => _upcasters.Add(upcaster);

    public bool TryGetHandler(string type, out ICommandHandler handler) =>
        _handlers.TryGetValue(type, out handler!);

    public JsonElement Upcast(string type, int version, JsonElement payload, int currentVersion)
    {
        var v = version;
        var current = payload;
        while (v < currentVersion)
        {
            var upcaster = _upcasters.SingleOrDefault(u =>
                u.Type == type && u.FromVersion == v && u.ToVersion == v + 1);
            if (upcaster is null)
            {
                throw new InvalidOperationException($"Missing upcaster {type} v{v}->v{v + 1}");
            }

            current = upcaster.Upcast(current);
            v++;
        }

        return current;
    }

    public IReadOnlyDictionary<string, int> MinCommandVersions =>
        _handlers.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentVersion, StringComparer.Ordinal);
}
