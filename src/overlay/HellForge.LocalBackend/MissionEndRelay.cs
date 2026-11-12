namespace HellForge.LocalBackend;

// Between MissionService.End and multiplayer (MissionEndShare): a mission this backend paid, which the other players'
// backends credit too. The joiner's game often never POSTs /Operation/Mission/end to its own LocalBackend — only the
// host's does — so without this the joiner keeps experience 1 after co-op.
public sealed class MissionEndRelay
{
    // Samples: the squad extract pool when this player extracted, else empty. Packages: their progressedPackages.
    public sealed record ParticipantEnd(IReadOnlyDictionary<uint, long> Samples, IReadOnlyList<(uint Package, int Factor)> Packages);

    // A mission paid here: id (dedupe), difficulty, success, succeeded objectives count.
    // Requisition/Medals null: the receiving backend uses its difficulty table (an older friend's frame).
    // Samples: the squad extract pool for this machine (empty if it did not extract). Packages: its weapon XP.
    // Peers: per Steam id, that same pool (extractors) or empty (left behind), plus that friend's packages.
    public sealed record EndedMission(string Mission, int Difficulty, bool Success, int Objectives, long? Requisition = null, long? Medals = null,
        IReadOnlyDictionary<uint, long>? Samples = null, IReadOnlyList<(uint Package, int Factor)>? Packages = null,
        IReadOnlyDictionary<ulong, ParticipantEnd>? Peers = null);

    public event Action<EndedMission>? Ended;

    // Credits this profile with a mission a friend's backend paid, once per mission id.
    public Func<EndedMission, bool>? Credit { get; set; }

    internal void OnEnded(EndedMission mission) => Ended?.Invoke(mission);
}
