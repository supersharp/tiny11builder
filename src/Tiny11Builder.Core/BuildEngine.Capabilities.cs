namespace Tiny11Builder.Core;

public sealed partial class BuildEngine
{
    private static async Task RemoveCapabilitiesAsync(BuildSession session, IReadOnlyList<string> names, bool batch,
        string stage, CancellationToken token, bool expert = false)
    {
        string Id(string name) => expert ? $"expert:{ImageItemKind.Capability}:{name}" : "capability:" + name;
        if (names.Count == 0) return;
        if (batch)
        {
            BatchRemovalResult? result = null;
            await session.Tracking.RunBatchAsync(names.Select(name => (Id(name), $"Request capability removal: {name}")).ToArray(),
                stage, async () =>
                {
                    result = await session.Dism.RemoveCapabilitiesAsync(session.Mount, names, token).ConfigureAwait(false);
                }, "capabilities", id => result?.Items.FirstOrDefault(item => Id(item.Name).Equals(id, StringComparison.OrdinalIgnoreCase))
                    is { } item ? Sentence("verified " + item.Describe()) : null).ConfigureAwait(false);
            RecordCapabilities(session, result!.Items);
        }
        else
            foreach (var name in names)
            {
                RemovalVerification? outcome = null;
                await session.Tracking.RunAsync(Id(name), stage, $"Request capability removal: {name}", async () =>
                {
                    var result = await session.Dism.RemoveCapabilitiesAsync(session.Mount, [name], token).ConfigureAwait(false);
                    outcome = result.Items.Single();
                }, () => Sentence("verified " + outcome!.Describe())).ConfigureAwait(false);
                RecordCapabilities(session, [outcome!]);
            }
    }

    private static void RecordCapabilities(BuildSession session, IReadOnlyList<RemovalVerification> outcomes)
    {
        foreach (var item in outcomes)
        {
            session.Capabilities.RemoveAll(prior => prior.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
            session.Capabilities.Add(item);
            session.Log($"Capability removal outcome: {item.Name} => {item.Describe()}.");
        }
    }

    private static void WarnDeferredCapabilities(BuildSession session)
    {
        var count = session.Capabilities.Count(item => item.Deferred);
        if (count > 0)
            session.Warn($"{count} capability removal(s) are deferred until first boot. They are not yet absent; their payloads are kept for pending servicing. See the capability outcomes in the build report.");
    }
}
