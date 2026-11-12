using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HellForge.LocalBackend;

// Superstore and other store fronts beyond the static GET /StoreFront list. The client opens the Superstore with:
//   GET /StoreFront/rotation     → { expireTime, salesPage } (diveharder / live salesPage.sections[].items[].mixId)
//   GET /StoreFront/2776696735   → that store front's page (id32 2776696735 in progression-data storeFront)
// Both were 501; without them the terminal polls and never shows stock. Prices stay on /Progression/items (Super Credits);
// Purchases turns those into Forge Credits.
public static class StoreFronts
{
    public const uint SuperstoreId = 2776696735;
    // Far enough that the client does not treat the page as expired mid-session.
    public const long DefaultExpireSeconds = 365L * 24 * 60 * 60;

    static readonly Lazy<Dictionary<uint, JsonObject>> Pages = new(Load);
    static readonly Lazy<HashSet<uint>> SuperstoreMixIds = new(() =>
    {
        if (!Pages.Value.TryGetValue(SuperstoreId, out var page) || page["sections"] is not JsonArray sections)
            return [];
        var ids = new HashSet<uint>();
        foreach (var section in sections.OfType<JsonObject>())
            if (section["items"] is JsonArray items)
                foreach (var item in items.OfType<JsonObject>())
                    if (item["mixId"] is JsonValue mix && uint.TryParse(mix.ToJsonString(), NumberStyles.None,
                            CultureInfo.InvariantCulture, out var id))
                        ids.Add(id);
        return ids;
    });

    public static bool IsSuperstoreItem(uint mixId) => SuperstoreMixIds.Value.Contains(mixId);

    public static bool TryGet(string path, out JsonNode body, out string outcome)
    {
        body = null!;
        outcome = "store_front";
        if (path.Equals("/StoreFront/rotation", StringComparison.Ordinal))
        {
            if (!Pages.Value.TryGetValue(SuperstoreId, out var page)) return false;
            body = new JsonObject
            {
                ["expireTime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + DefaultExpireSeconds,
                ["salesPage"] = page.DeepClone()
            };
            outcome = "store_front_rotation";
            return true;
        }
        if (!path.StartsWith("/StoreFront/", StringComparison.Ordinal)) return false;
        var idText = path["/StoreFront/".Length..];
        if (idText.Equals("buy", StringComparison.OrdinalIgnoreCase)) return false;
        if (!uint.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
            !Pages.Value.TryGetValue(id, out var found)) return false;
        body = found.DeepClone();
        outcome = "store_front_page";
        return true;
    }

    static Dictionary<uint, JsonObject> Load()
    {
        using var data = LocalBackend.LoadResource("progression-data.json");
        var pages = new Dictionary<uint, JsonObject>();
        foreach (var front in data.RootElement.GetProperty("storeFront").EnumerateArray())
        {
            if (!front.TryGetProperty("id32", out var id) || !id.TryGetUInt32(out var mix)) continue;
            pages[mix] = JsonNode.Parse(front.GetRawText())!.AsObject();
        }
        return pages;
    }
}
