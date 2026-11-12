using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HellForge.LocalBackend;

// Local operation and mission lifecycle. Request and reply shapes were decoded from the client:
//   POST /Operation/Create          0xaa641804  body {operations:[{id, seed, warId, planetIndex, campaignId, type,
//                                               difficulty, race, ...missions}]}, reply {operations:[{id, rewards, backupRewards}]} (parser 0x105d9d0)
//   POST /Operation/Mission/Start   0xc50b20e7  reply {missionId: string} (parser 0x107d980)
//   POST /Operation/Mission/end     0x1e555ea6  body from 0x11e9680 (missionSuccess, difficultyType, objectives,
//                                               lootedItems, heavyLootedItems, ...), reply parsed by 0x105e3e0/0x105e6b0
//   POST /Operation/Mission/Activity 0x385dc715 builder 0x12c2940 {missionId, activityId32, objectId, participants:[account
//                                               ids], gmiStr}, sent once every player confirmed a pickup; reply {rewards:[
//                                               {platformAccountId: string, rewards:[{mixId, amount}] (15 at most)}]} (parsers
//                                               0x105e010, 0x105e190, 0x1044660). The client adds the local player's entry
//                                               (0x1370a20) and forwards each other one to that player's game (0xA0823945).
//   POST /Operation/Mission/SetMaxRewards, POST /Operation/Abandon: no reply fields are read.
//   POST /Progression/inventory/consume  0xeea479dc  body {consumedItems:[{mixId, amount}]} sent as a mission ends (the
//                                               dispatcher has no reply case for it): acknowledged, nothing taken.
// Rewards are local rules, not Arrowhead's: XP by difficulty, success and objectives; requisition; medals for a won
// mission; samples are the squad extract pool (every extracted player gets the sum of extracted lootedItems). Level 11
// (a mod's, native/mod-loader/difficulty.c) pays half as much XP and medals again as level 10. Each mission id is paid
// once (ProfileStore.ApplyMissionRewards).
public sealed class MissionService
{
    public const uint RequisitionMixId = 0xD7150EEC;
    // The medals a player holds and spends on warbond pages (every warbond's medalsConsumableMixId; catalogue maxCap 250).
    public const uint MedalsMixId = 1276729503;
    // The "medals" reward item the live server names in its rewards (major orders, medal packages); the client turns it
    // into MedalsMixId when it adds it (game.dll 0x136efa8). Shown on the operation's reward card.
    public const uint MedalRewardMixId = 0x3584C850;
    static readonly int[] DifficultyExperience = [100, 150, 200, 300, 400, 500, 650, 800, 1000, 1200, 1500, 2250];
    static readonly int[] DifficultyMedals = [0, 2, 2, 3, 4, 5, 6, 7, 8, 9, 10, 15];
    // /Mission/RewardEntries: each badge id32 pays mixId * amount. The results screen totals those itself.
    // The reply's badges list must stay empty: parser 0x105e3e0 rejects a filled copy (network error, no XP/medals on the
    // summary). Pay the badge mixes in rewards[] and the profile instead.

    readonly ProfileStore profile;
    readonly ConcurrentDictionary<long, int> operationDifficulty = new();
    readonly PickupRelay? pickups;
    readonly MissionEndRelay? missionEnds;
    readonly Func<ulong?>? localAccount;

    public MissionService(ProfileStore profile, PickupRelay? pickups = null, MissionEndRelay? missionEnds = null, Func<ulong?>? localAccount = null)
    {
        this.profile = profile;
        this.pickups = pickups;
        this.missionEnds = missionEnds;
        this.localAccount = localAccount;
        if (pickups != null) pickups.Credit = Credit;
        if (missionEnds != null) missionEnds.Credit = CreditEnd;
    }

    public static bool Handles(string path) => path is "/Operation/Create" or "/Operation/Abandon" or "/Operation/Mission/Start"
        or "/Operation/Mission/end" or "/Operation/Mission/Activity" or "/Operation/Mission/SetMaxRewards" or "/Progression/inventory/consume";

    public (JsonNode Reply, string Outcome) Handle(string path, JsonElement body) => path switch
    {
        "/Operation/Create" => (Create(body), "operation_created"),
        "/Operation/Mission/Start" => (new JsonObject { ["missionId"] = Guid.NewGuid().ToString() }, "mission_started"),
        "/Operation/Mission/end" => (End(body), "mission_ended"),
        "/Operation/Mission/Activity" => (Activity(body), "mission_activity"),
        "/Progression/inventory/consume" => (new JsonObject(), "items_consumed"),
        _ => (new JsonObject(), "acknowledged")
    };

    // The pickups' activities (hash32 of "mission_activity_<type>", set at spawn by 0x593d30): a Super Credits pile (Forge
    // Credits), medals and requisition slips.
    public const uint ForgeCreditsActivity = 0x7F8FCE26, MedalsActivity = 0x0A2ECD4E, RequisitionActivity = 0x7549A744;
    // What a pile pays: the item the profile holds (medals as MedalsMixId), the one the reply names (medals as the reward
    // item MedalRewardMixId, which the client turns into its medals) and the amount; null for another activity.
    static (uint Held, uint Shown, long Amount)? PileOf(uint activity, long pile) => activity switch
    {
        ForgeCreditsActivity => (PaidContentSet.ForgeCreditsWallet, PaidContentSet.ForgeCreditsWallet, Rewards.ForgeCredits(pile)),
        MedalsActivity => (MedalsMixId, MedalRewardMixId, Rewards.MedalPickup(pile)),
        RequisitionActivity => (RequisitionMixId, RequisitionMixId, Rewards.RequisitionPickup(pile)),
        _ => null
    };

    // A pickup: every participant gets the same amount, as on the live game. This profile is credited here, once per pile
    // (ProfileStore.ApplyPickup), and the other participants' backends through multiplayer (PickupRelay); the reply names
    // what the client adds, and the client forwards the others' entries to their games.
    JsonObject Activity(JsonElement body)
    {
        var rewards = new JsonArray();
        if (body.ValueKind != JsonValueKind.Object || !TryInt(body, "activityId32", out var activity) || !TryInt(body, "objectId", out var pile))
            return new JsonObject { ["rewards"] = rewards };
        var mission = body.TryGetProperty("missionId", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "";
        if (PileOf((uint)activity, pile) is not { } paid || !profile.ApplyPickup(mission, pile, paid.Held, paid.Amount))
            return new JsonObject { ["rewards"] = rewards };
        var accounts = Participants(body);
        foreach (var account in accounts)
            rewards.Add((JsonNode)new JsonObject
            {
                ["platformAccountId"] = account.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["rewards"] = new JsonArray((JsonNode)new JsonObject { ["mixId"] = paid.Shown, ["amount"] = paid.Amount })
            });
        pickups?.OnPaid(new PickupRelay.Pickup(mission, pile, (uint)activity, accounts));
        return new JsonObject { ["rewards"] = rewards };
    }

    // The request's participants: account ids as numbers or decimal strings, each once, 16 at most.
    static List<ulong> Participants(JsonElement body)
    {
        var accounts = new List<ulong>();
        if (!body.TryGetProperty("participants", out var participants) || participants.ValueKind != JsonValueKind.Array) return accounts;
        foreach (var participant in participants.EnumerateArray().Take(16))
            if (participant.ValueKind is JsonValueKind.Number or JsonValueKind.String &&
                ulong.TryParse(participant.ValueKind == JsonValueKind.String ? participant.GetString() : participant.GetRawText(),
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var account) && !accounts.Contains(account))
                accounts.Add(account);
        return accounts;
    }

    static readonly Dictionary<uint, (uint Mix, long Per)[]> BadgePayouts = LoadBadgePayouts();

    static Dictionary<uint, (uint Mix, long Per)[]> LoadBadgePayouts()
    {
        using var document = LocalBackend.LoadResource("mission-reward-entries.json");
        var payouts = new Dictionary<uint, (uint Mix, long Per)[]>();
        foreach (var badge in document.RootElement.GetProperty("rewardEntries").GetProperty("badges").EnumerateArray())
        {
            if (!badge.TryGetProperty("itemRewards", out var rewards) || rewards.ValueKind != JsonValueKind.Array || rewards.GetArrayLength() == 0)
                continue;
            var paid = new List<(uint Mix, long Per)>();
            foreach (var reward in rewards.EnumerateArray())
            {
                if (!reward.TryGetProperty("mixId", out var mixEl) || !mixEl.TryGetUInt32(out var mix) ||
                    !reward.TryGetProperty("amount", out var amountEl) || !amountEl.TryGetInt64(out var per) ||
                    PaidContentSet.IsSuperCredits(mix)) continue;
                paid.Add((mix == MedalRewardMixId ? MedalsMixId : mix, per));
            }
            if (paid.Count > 0) payouts[badge.GetProperty("id32").GetUInt32()] = [.. paid];
        }
        return payouts;
    }

    // True when the client named badges that this table can pay. The reply does not echo the list.
    static bool ApplyReportedBadges(JsonElement body, Dictionary<uint, long> grants)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("badges", out var list) || list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() == 0) return false;
        var named = false;
        foreach (var badge in list.EnumerateArray())
        {
            if (!TryInt(badge, "id32", out var id) || !TryInt(badge, "amount", out var count) || id <= 0) continue;
            count = Math.Clamp(count, 0, 10_000);
            named = true;
            if (!BadgePayouts.TryGetValue((uint)id, out var payouts)) continue;
            foreach (var (mix, per) in payouts)
                grants[mix] = grants.GetValueOrDefault(mix) + per * count;
        }
        return named;
    }

    // A pile a friend's backend paid: this profile gets the same amount, worked out here from the pile, once.
    bool Credit(PickupRelay.Pickup pickup) =>
        PileOf(pickup.Activity, pickup.Pile) is { } paid && profile.ApplyPickup(pickup.Mission, pickup.Pile, paid.Held, paid.Amount);

    // A mission a friend's backend paid (MissionEndShare): same XP / medals / requisition rules, once per mission id.
    // Samples are the squad extract pool (the host's Mission/end names each pocket; we sum extracted players). Weapon
    // packages stay that friend's progressedPackages. The joiner's game often never POSTs /Operation/Mission/end.
    bool CreditEnd(MissionEndRelay.EndedMission end)
    {
        var experience = Rewards.Experience(end.Difficulty, end.Success, end.Objectives);
        var grants = new Dictionary<uint, long>();
        if (end.Success)
        {
            var requisition = end.Requisition ?? Rewards.Requisition(end.Difficulty);
            var medals = end.Medals ?? Rewards.Medals(end.Difficulty);
            if (requisition > 0) grants[RequisitionMixId] = requisition;
            if (medals > 0) grants[MedalsMixId] = medals;
        }
        if (end.Samples != null)
            foreach (var (mix, amount) in end.Samples)
                if (amount > 0) grants[mix] = grants.GetValueOrDefault(mix) + amount;
        if (!profile.ApplyMissionRewards(experience, grants, end.Mission)) return false;
        if (end.Packages is { Count: > 0 })
            profile.ApplyPackageGains(end.Packages, Rewards.WeaponExperience(experience));
        return true;
    }

    JsonObject Create(JsonElement body)
    {
        var operations = new JsonArray();
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("operations", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var operation in list.EnumerateArray())
            {
                if (!TryInt(operation, "id", out var id)) continue;
                var level = TryInt(operation, "difficulty", out var difficulty) ? (int)difficulty : 1;
                operationDifficulty[id] = level;
                // The planet card's reward: the medals the operation's missions pay when all are won (parser 0x10558f0).
                var missions = operation.TryGetProperty("missions", out var listed) && listed.ValueKind == JsonValueKind.Array ? listed.GetArrayLength() : 0;
                var medals = Rewards.Medals(level) * Math.Max(1, missions);
                operations.Add((JsonNode)new JsonObject
                {
                    ["id"] = id,
                    ["rewards"] = new JsonArray((JsonNode)new JsonObject { ["type"] = 1, ["id32"] = MedalRewardMixId, ["amount"] = medals, ["isFullReward"] = true }),
                    ["backupRewards"] = new JsonArray()
                });
            }
        return new JsonObject { ["operations"] = operations };
    }

    JsonObject End(JsonElement body)
    {
        var success = FindBool(body, "missionSuccess") ?? FindBool(body, "succeeded") ?? false;
        var difficulty = (int)(FindInt(body, "difficultyType") ?? (FindInt(body, "operationId") is { } op &&
            operationDifficulty.TryGetValue(op, out var known) ? known : 1));
        var objectives = CountSucceededObjectives(body);
        var experience = Rewards.Experience(difficulty, success, objectives);
        var (peers, squadLoot) = ReadSquad(body);
        var local = LocalParticipantElement(body, FindBool(body, "isOperationOwner") == true);
        Dictionary<uint, long> looted;
        List<(uint Package, int Factor)> packages;
        // Prefer the local participant row. If Steam id is known but the row was missed, use the squad map keyed by that
        // id (same extract pool / packages). Never mark a co-op end paid with an empty guess: mission.ended still has to
        // CreditEnd, and paying zero here would make that share a no-op ("already paid") — samples look earned on the
        // results screen then vanish after a relaunch.
        var attributed = false;
        if (local != null)
        {
            // Live game: whoever carried samples onto the Pelican, every extractor receives the full squad total.
            looted = Extracted(local.Value) ? squadLoot : [];
            packages = ReadPackages(local.Value);
            attributed = true;
        }
        else if (localAccount?.Invoke() is { } me && peers.TryGetValue(me, out var self))
        {
            looted = new Dictionary<uint, long>(self.Samples);
            packages = [.. self.Packages];
            attributed = true;
        }
        else if (peers.Count > 0)
        {
            looted = [];
            packages = [];
        }
        else
        {
            looted = SumItems(body, "lootedItems");
            packages = ReadPackages(body);
            attributed = true;
        }
        var heavy = SumItems(body, "heavyLootedItems");
        var grants = new Dictionary<uint, long>();
        foreach (var source in new[] { looted, heavy })
            foreach (var (mix, amount) in source) grants[mix] = grants.GetValueOrDefault(mix) + amount;
        var fromBadges = success && ApplyReportedBadges(body, grants);
        var requisition = fromBadges ? Math.Max(0, grants.GetValueOrDefault(RequisitionMixId)) : (success ? Rewards.Requisition(difficulty) : 0);
        var medals = fromBadges ? Math.Max(0, grants.GetValueOrDefault(MedalsMixId)) : (success ? Rewards.Medals(difficulty) : 0);
        if (!fromBadges)
        {
            if (requisition > 0) grants[RequisitionMixId] = grants.GetValueOrDefault(RequisitionMixId) + requisition;
            if (medals > 0) grants[MedalsMixId] = grants.GetValueOrDefault(MedalsMixId) + medals;
        }
        else
        {
            if (grants.GetValueOrDefault(RequisitionMixId) < 0) grants[RequisitionMixId] = 0;
            if (grants.GetValueOrDefault(MedalsMixId) < 0) grants[MedalsMixId] = 0;
        }
        static JsonArray Items(Dictionary<uint, long> items) =>
            new(items.Select(item => (JsonNode?)new JsonObject { ["mixId"] = item.Key, ["amount"] = item.Value }).ToArray());
        var rewards = new JsonArray();
        if (requisition > 0) rewards.Add((JsonNode)new JsonObject { ["mixId"] = RequisitionMixId, ["amount"] = requisition });
        if (medals > 0) rewards.Add((JsonNode)new JsonObject { ["mixId"] = MedalRewardMixId, ["amount"] = medals });
        // A second Mission/end for the same id must not pay the profile again, but the reply stays the same:
        // the results screen replaces its running total with this body, and an empty one wipes the payout.
        var missionId = FindString(body, "missionId");
        // Unattributed co-op ends (joiner body, Steam id not ready): answer the client, do not write paidMissions —
        // the host's mission.ended must still be allowed to CreditEnd the real squad pool.
        var paid = false;
        if (attributed)
        {
            paid = profile.ApplyMissionRewards(experience, grants, missionId);
            if (paid && packages.Count > 0) profile.ApplyPackageGains(packages, Rewards.WeaponExperience(experience));
            if (paid && !string.IsNullOrEmpty(missionId))
                missionEnds?.OnEnded(new MissionEndRelay.EndedMission(missionId, difficulty, success, objectives, requisition, medals, looted, packages, peers));
        }
        // Full packageProgress snapshot: the client never refetches GET /Progression in-session, and a partial
        // progressionPackageUpdates list can leave other weapons at the login snapshot (or zero) until relaunch.
        var packageUpdates = profile.CopyPackageProgress();
        return new JsonObject
        {
            ["operationId"] = FindInt(body, "operationId") ?? 0, ["missionIndex"] = FindInt(body, "missionIndex") ?? 0,
            ["missionId"] = missionId ?? "", ["experience"] = attributed ? experience : 0, ["multipliers"] = new JsonArray(),
            ["badges"] = new JsonArray(), ["campaignWon"] = false, ["impact"] = 0, ["othersImpactPlanet"] = 0,
            ["othersImpactCommunityTarget"] = 0, ["othersImpactPlanetEvent"] = 0, ["progressionPackageUpdates"] = packageUpdates,
            // Heavy loot is granted above, but its reply list wants {id32, rewards: [...]} per heavy item (parser 0x105e6b0;
            // {mixId, amount} there made the client refuse the whole reply), and which id32 is not known yet.
            ["lootedItemRewards"] = Items(looted), ["lootedHeavyItemRewards"] = new JsonArray(),
            ["rewards"] = rewards,
            ["repeatingPlayer"] = false
        };
    }

    public static class Rewards
    {
        public static long Experience(int difficulty, bool success, int objectives)
        {
            var base_ = DifficultyExperience[Math.Clamp(difficulty, 0, DifficultyExperience.Length - 1)];
            return (success ? base_ : base_ / 2) + 50L * Math.Clamp(objectives, 0, 20);
        }

        // A Forge Credits pile: the live game's amounts are the server's (the client sends none), so HellForge's own: mostly 10,
        // sometimes 20, 50 or 100, the same for a pile wherever it is asked (its objectId).
        public static long ForgeCredits(long pile) => ((uint)(unchecked((ulong)pile * 0x9E3779B97F4A7C15UL) >> 32) % 100) switch
        {
            < 75 => 10,
            < 92 => 20,
            < 98 => 50,
            _ => 100
        };

        // A medal pickup: mostly 1, sometimes 2, now and then 5; requisition slips: 50, 100 or 200 the same way. Per pile, as
        // ForgeCredits.
        public static long MedalPickup(long pile) => Bucket(pile) switch { < 75 => 1, < 95 => 2, _ => 5 };
        public static long RequisitionPickup(long pile) => Bucket(pile) switch { < 75 => 50, < 95 => 100, _ => 200 };
        static uint Bucket(long pile) => (uint)(unchecked((ulong)pile * 0x9E3779B97F4A7C15UL) >> 32) % 100;

        // Level 11 is a mod's (native/mod-loader/difficulty.c, HfDifficulty).
        public static long Requisition(int difficulty) => 200L * Math.Clamp(difficulty, 1, 11);

        // A won mission's medals.
        public static long Medals(int difficulty) => DifficultyMedals[Math.Clamp(difficulty, 1, DifficultyMedals.Length - 1)];

        // Weapon mastery XP for one progressedPackages factor. The live server uses kills; the client only names the
        // package and a factor, so this is a local rule: a quarter of the mission's player XP (at least 1).
        public static long WeaponExperience(long missionExperience, int factor = 1) =>
            Math.Max(1, missionExperience / 4) * Math.Clamp(factor, 1, 10);
    }

    static int CountSucceededObjectives(JsonElement body)
    {
        var count = 0;
        foreach (var objectives in FindAll(body, "objectives"))
            if (objectives.ValueKind == JsonValueKind.Array)
                foreach (var objective in objectives.EnumerateArray())
                    if (objective.ValueKind == JsonValueKind.Object && objective.TryGetProperty("succeeded", out var s) && s.ValueKind == JsonValueKind.True) count++;
        return count;
    }

    // Extracted pockets become one squad pool; each extractor's share is that pool plus their own weapon packages.
    static (Dictionary<ulong, MissionEndRelay.ParticipantEnd> Peers, Dictionary<uint, long> SquadLoot) ReadSquad(JsonElement body)
    {
        var listed = new List<(ulong Account, bool Extracted, Dictionary<uint, long> Loot, List<(uint Package, int Factor)> Packages)>();
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("participants", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var participant in list.EnumerateArray())
            {
                if (participant.ValueKind != JsonValueKind.Object || !participant.TryGetProperty("platformAccountId", out var id)) continue;
                var text = id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();
                if (text == null || !ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var account))
                    continue;
                listed.Add((account, Extracted(participant), SumItems(participant, "lootedItems"), ReadPackages(participant)));
            }
        var squadLoot = new Dictionary<uint, long>();
        foreach (var row in listed)
            if (row.Extracted)
                foreach (var (mix, amount) in row.Loot)
                    squadLoot[mix] = squadLoot.GetValueOrDefault(mix) + amount;
        var peers = new Dictionary<ulong, MissionEndRelay.ParticipantEnd>();
        foreach (var row in listed)
            peers[row.Account] = new MissionEndRelay.ParticipantEnd(row.Extracted ? squadLoot : [], row.Packages);
        return (peers, squadLoot);
    }

    static bool Extracted(JsonElement participant) =>
        !participant.TryGetProperty("extracted", out var flag) || flag.ValueKind != JsonValueKind.False;

    static List<(uint Package, int Factor)> ReadPackages(JsonElement node)
    {
        var packages = new List<(uint Package, int Factor)>();
        foreach (var list in FindAll(node, "progressedPackages"))
            if (list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (TryInt(item, "id32", out var id) && id > 0 && id <= uint.MaxValue)
                    {
                        var factor = TryInt(item, "factor", out var named) ? (int)Math.Clamp(named, 1, 10) : 1;
                        packages.Add(((uint)id, factor));
                    }
        return packages;
    }

    JsonElement? LocalParticipantElement(JsonElement body, bool operationOwner)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("participants", out var list) || list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() == 0) return null;
        var me = localAccount?.Invoke();
        foreach (var participant in list.EnumerateArray())
        {
            if (participant.ValueKind != JsonValueKind.Object || !participant.TryGetProperty("platformAccountId", out var id)) continue;
            var text = id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();
            if (me is { } steam && text != null && ulong.TryParse(text, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var account) && account == steam)
                return participant;
        }
        return operationOwner ? list[0] : null;
    }

    static Dictionary<uint, long> SumItems(JsonElement body, string key)
    {
        var items = new Dictionary<uint, long>();
        foreach (var list in FindAll(body, key))
            if (list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    // Super Credits found on a map (or claimed by a modified report) are a premium currency: never granted.
                    if (TryInt(item, "mixId", out var mix) && TryInt(item, "amount", out var amount) && mix > 0 && amount > 0 && mix <= uint.MaxValue &&
                        !PaidContentSet.IsSuperCredits((uint)mix))
                        items[(uint)mix] = items.GetValueOrDefault((uint)mix) + Math.Min(amount, 10_000);
        return items;
    }

    // The client's report nests per-player data; search the whole document for a key.
    static IEnumerable<JsonElement> FindAll(JsonElement node, string key)
    {
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject())
                foreach (var found in FindIn(property, key)) yield return found;
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var element in node.EnumerateArray())
                foreach (var nested in FindAll(element, key)) yield return nested;
    }

    // A property's value when it is the key, then the key's values inside it.
    static IEnumerable<JsonElement> FindIn(JsonProperty property, string key)
    {
        if (property.NameEquals(key)) yield return property.Value;
        foreach (var nested in FindAll(property.Value, key)) yield return nested;
    }

    static long? FindInt(JsonElement body, string key)
    {
        foreach (var value in FindAll(body, key))
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return null;
    }

    static bool? FindBool(JsonElement body, string key)
    {
        foreach (var value in FindAll(body, key))
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.ValueKind == JsonValueKind.True;
        return null;
    }

    static string? FindString(JsonElement body, string key)
    {
        foreach (var value in FindAll(body, key))
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }

    static bool TryInt(JsonElement node, string key, out long value)
    {
        value = 0;
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value);
    }
}
