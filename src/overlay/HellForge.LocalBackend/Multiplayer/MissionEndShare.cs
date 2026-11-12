namespace HellForge.LocalBackend.Multiplayer;

// Mission end in co-op (MissionEndRelay): the game often sends Mission/end only to the host's LocalBackend. This tells
// the other backends in the lobby so they ApplyMissionRewards too (XP, medals, requisition, the squad sample pool),
// once per mission id. Extractors each receive the same lootedItems sum; weapon packages stay per player.
//   "mission.ended" [mission id, difficulty, success 0|1, objectives, requisition, medals, loot mix=amount,..., packages id*factor,...]
//   host:   sends to every friend in lobbies hosted here
//   joiner: sends to the host of the lobby it joined (rare: when its own game posts end)
//   host:   from a member: credits here and forwards to the other hosted friends
//   joiner: from its host: credits here
public sealed class MissionEndShare
{
    public const string FrameType = "mission.ended";
    const int MaxMissionChars = 128;
    readonly IPeerTransport transport;
    readonly LobbyService lobby;
    readonly MissionEndRelay relay;
    readonly Action<string> log;

    public MissionEndShare(IPeerTransport transport, LobbyService lobby, MissionEndRelay relay, Action<string> log)
    {
        (this.transport, this.lobby, this.relay, this.log) = (transport, lobby, relay, log);
        relay.Ended += Ended;
    }

    void Ended(MissionEndRelay.EndedMission mission)
    {
        var local = transport.LocalId;
        var hosts = lobby.JoinedHosts();
        var targets = hosts.Count > 0 ? hosts.ToList() : lobby.HostedFriends().Where(p => p != local).ToList();
        if (targets.Count == 0) return;
        var sampleTotal = 0L;
        foreach (var target in targets)
        {
            var share = mission;
            if (mission.Peers != null && mission.Peers.TryGetValue(target, out var peer))
                share = mission with { Samples = peer.Samples, Packages = peer.Packages };
            else if (mission.Peers != null)
                // Keep the host report's sample pool when the Steam id is missing from participants (do not send empty
                // and make the friend's profile skip a haul the results screen already showed).
                share = mission with { Samples = mission.Samples ?? new Dictionary<uint, long>(), Packages = [] };
            if (share.Samples != null)
                foreach (var amount in share.Samples.Values) sampleTotal += amount;
            transport.Send(target, Encode(share).Encode(), reliable: true);
        }
        log($"Mission end: told {targets.Count} friend(s)' backend(s) of mission {mission.Mission} (difficulty {mission.Difficulty}, success {mission.Success}, sample units {sampleTotal}).");
    }

    public void FromPeer(ulong peer, Frame frame)
    {
        if (frame.Type != FrameType) return;
        MissionEndRelay.EndedMission mission;
        try { mission = Decode(frame); }
        catch (InvalidDataException error) { log("Mission end: dropped a malformed frame: " + error.Message); return; }
        bool fromHost = lobby.HostsJoinedLobby(peer), fromMember = lobby.InHostedLobby(peer);
        if (!fromHost && !fromMember)
        {
            log($"Mission end: ignored mission {mission.Mission} from a machine not in a lobby with this one.");
            return;
        }
        log(relay.Credit?.Invoke(mission) == true
            ? $"Mission end: credited mission {mission.Mission} (difficulty {mission.Difficulty}, success {mission.Success}) a friend finished."
            : $"Mission end: mission {mission.Mission} already paid here.");
        if (!fromMember) return;
        var bytes = frame.Encode();
        var local = transport.LocalId;
        foreach (var other in lobby.HostedFriends().Where(p => p != peer && p != local))
            transport.Send(other, bytes, reliable: true);
    }

    static Frame Encode(MissionEndRelay.EndedMission mission) =>
        new Frame(FrameType)
            .Add(mission.Mission)
            .Add(mission.Difficulty)
            .Add(mission.Success ? 1 : 0)
            .Add(mission.Objectives)
            .Add(mission.Requisition ?? -1)
            .Add(mission.Medals ?? -1)
            .Add(EncodePairs(mission.Samples, '='))
            .Add(EncodePackages(mission.Packages));

    static MissionEndRelay.EndedMission Decode(Frame frame)
    {
        var mission = frame.Text(0);
        if (mission.Length > MaxMissionChars) throw new InvalidDataException("mission id too long");
        var difficulty = frame.Int(1);
        if (difficulty is < 0 or > 20) throw new InvalidDataException("bad difficulty");
        var success = frame.Int(2);
        if (success is not (0 or 1)) throw new InvalidDataException("bad success");
        var objectives = frame.Int(3);
        if (objectives is < 0 or > 20) throw new InvalidDataException("bad objectives");
        long? requisition = null, medals = null;
        if (frame.Count > 4)
        {
            var slips = frame.Int(4);
            var paid = frame.Int(5);
            if (slips is < -1 or > 100_000 || paid is < -1 or > 1000) throw new InvalidDataException("bad mission payout");
            if (slips >= 0) requisition = slips;
            if (paid >= 0) medals = paid;
        }
        Dictionary<uint, long>? samples = null;
        List<(uint Package, int Factor)>? packages = null;
        if (frame.Count > 6)
        {
            samples = DecodePairs(frame.Text(6), '=', 16, 10_000);
            packages = frame.Count > 7 ? DecodePackages(frame.Text(7)) : [];
        }
        return new MissionEndRelay.EndedMission(mission, difficulty, success == 1, objectives, requisition, medals, samples, packages);
    }

    static string EncodePairs(IReadOnlyDictionary<uint, long>? items, char sep)
    {
        if (items == null || items.Count == 0) return "";
        return string.Join(",", items.OrderBy(item => item.Key).Select(item =>
            item.Key.ToString(System.Globalization.CultureInfo.InvariantCulture) + sep +
            item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    static string EncodePackages(IReadOnlyList<(uint Package, int Factor)>? packages)
    {
        if (packages == null || packages.Count == 0) return "";
        return string.Join(",", packages.Select(item =>
            item.Package.ToString(System.Globalization.CultureInfo.InvariantCulture) + "*" +
            item.Factor.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    static Dictionary<uint, long> DecodePairs(string text, char sep, int maxItems, long maxAmount)
    {
        var items = new Dictionary<uint, long>();
        if (string.IsNullOrEmpty(text)) return items;
        foreach (var part in text.Split(','))
        {
            if (items.Count >= maxItems) throw new InvalidDataException("too many loot entries");
            var at = part.IndexOf(sep);
            if (at <= 0 || !uint.TryParse(part[..at], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mix) ||
                !long.TryParse(part[(at + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var amount) ||
                mix == 0 || amount > maxAmount)
                throw new InvalidDataException("bad loot");
            items[mix] = amount;
        }
        return items;
    }

    static List<(uint Package, int Factor)> DecodePackages(string text)
    {
        var packages = new List<(uint Package, int Factor)>();
        if (string.IsNullOrEmpty(text)) return packages;
        foreach (var part in text.Split(','))
        {
            if (packages.Count >= 16) throw new InvalidDataException("too many packages");
            var at = part.IndexOf('*');
            if (at <= 0 || !uint.TryParse(part[..at], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ||
                !int.TryParse(part[(at + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var factor) ||
                id == 0 || factor is < 1 or > 10)
                throw new InvalidDataException("bad package");
            packages.Add((id, factor));
        }
        return packages;
    }
}
