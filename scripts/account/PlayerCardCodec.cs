using System;
using System.IO;
using System.Text;
using System.Text.Json;

/// <summary>
/// Turns a PlayerCard into JSON and back. The same format is the save file on disk and the message
/// two players swap over the LAN. Pure C#, and deliberately written with the low-level Utf8JsonWriter
/// and JsonDocument instead of reflection-based serialisation, so it behaves the same in every
/// export (desktop and Android) with no trimming surprises.
///
/// Decoding NEVER trusts its input - a hand-edited save, a half-written file and a hostile peer all
/// look alike from here. Every number is clamped, every string is sanitised and length-limited,
/// unknown medals are ignored, tables are size-capped, and any parse problem simply returns false.
/// It never throws.
///
/// Format (version 1):
/// {
///   "version": 1, "id": "...", "name": "Ash", "avatar": { "emblem": 3, "color": 5 },
///   "created": 1790000000, "setup_done": true,                 (setup_done only on disk)
///   "stats": { "wins": 409, "ties": 2, "losses": 234, "kills": 14673, "deaths": 6690,
///              "playtime_seconds": 233640.0,
///              "weapons": { "AK-47": { "kills": 49, "deaths": 6 }, ... },
///              "medals":  { "clear_kill": 10338, ... } }
/// }
/// </summary>
public static class PlayerCardCodec
{
    public const int Version = 1;

    /// <summary>Longest JSON text Decode will even look at (a real card is about 1.5 KB).</summary>
    public const int MaxJsonChars = 16384;

    // ---- encode -----------------------------------------------------------------------------

    /// <param name="forDisk">True for the save file (indented, includes local-only fields). False
    /// for the wire: compact, and nothing private.</param>
    public static string Encode(PlayerCard card, bool forDisk)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = forDisk }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", Version);
            w.WriteString("id", card.AccountId);
            w.WriteString("name", card.Name);
            w.WriteStartObject("avatar");
            w.WriteNumber("emblem", card.AvatarEmblem);
            w.WriteNumber("color", card.AvatarColor);
            w.WriteEndObject();
            w.WriteNumber("created", card.CreatedUnix);
            if (forDisk) w.WriteBoolean("setup_done", card.SetupDone);

            CareerStats s = card.Stats;
            w.WriteStartObject("stats");
            w.WriteNumber("wins", s.Wins);
            w.WriteNumber("ties", s.Ties);
            w.WriteNumber("losses", s.Losses);
            w.WriteNumber("kills", s.Kills);
            w.WriteNumber("deaths", s.Deaths);
            w.WriteNumber("playtime_seconds", Math.Round(s.PlaytimeSeconds, 1));

            w.WriteStartObject("weapons");
            foreach (var entry in s.Weapons)
            {
                w.WriteStartObject(entry.Key);
                w.WriteNumber("kills", entry.Value.Kills);
                w.WriteNumber("deaths", entry.Value.Deaths);
                w.WriteEndObject();
            }
            w.WriteEndObject();

            w.WriteStartObject("medals");
            foreach (MedalCatalog.Info info in MedalCatalog.All)
            {
                long count = s.MedalCount(info.Medal);
                if (count > 0 || forDisk) w.WriteNumber(info.Key, count);
            }
            w.WriteEndObject();

            w.WriteEndObject(); // stats
            w.WriteEndObject(); // card
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ---- decode -----------------------------------------------------------------------------

    public static bool TryDecode(string? json, out PlayerCard? card)
    {
        card = null;
        if (string.IsNullOrWhiteSpace(json) || json!.Length > MaxJsonChars) return false;

        try
        {
            var options = new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = true };
            using JsonDocument document = JsonDocument.Parse(json, options);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var result = new PlayerCard();
            result.AccountId = SanitizeId(ReadString(root, "id"));
            string name = AccountRules.SanitizeName(ReadString(root, "name"));
            result.Name = name.Length > 0 ? name : AccountRules.FallbackName;
            result.CreatedUnix = ReadLong(root, "created", 0, 4_102_444_800L);
            result.SetupDone = root.TryGetProperty("setup_done", out JsonElement setup)
                && setup.ValueKind == JsonValueKind.True;

            if (root.TryGetProperty("avatar", out JsonElement avatar) && avatar.ValueKind == JsonValueKind.Object)
            {
                result.AvatarEmblem = AvatarCatalog.ClampEmblem((int)ReadLong(avatar, "emblem", 0, 1000));
                result.AvatarColor = AvatarCatalog.ClampColor((int)ReadLong(avatar, "color", 0, 1000));
            }

            if (root.TryGetProperty("stats", out JsonElement stats) && stats.ValueKind == JsonValueKind.Object)
                ReadStats(stats, result.Stats);

            card = result;
            return true;
        }
        catch (Exception)
        {
            // JsonException for malformed text, plus anything unforeseen: a bad card must never
            // take the game down.
            card = null;
            return false;
        }
    }

    private static void ReadStats(JsonElement stats, CareerStats into)
    {
        into.Wins = ReadLong(stats, "wins", 0, CareerStats.MaxCounter);
        into.Ties = ReadLong(stats, "ties", 0, CareerStats.MaxCounter);
        into.Losses = ReadLong(stats, "losses", 0, CareerStats.MaxCounter);
        into.Kills = ReadLong(stats, "kills", 0, CareerStats.MaxCounter);
        into.Deaths = ReadLong(stats, "deaths", 0, CareerStats.MaxCounter);
        into.PlaytimeSeconds = ReadDouble(stats, "playtime_seconds", 0.0, 3.0e9);

        if (stats.TryGetProperty("weapons", out JsonElement weapons) && weapons.ValueKind == JsonValueKind.Object)
        {
            int seen = 0;
            foreach (JsonProperty weapon in weapons.EnumerateObject())
            {
                if (++seen > CareerStats.MaxWeapons) break;
                if (weapon.Value.ValueKind != JsonValueKind.Object) continue;
                string weaponName = AccountRules.SanitizeName(weapon.Name, CareerStats.MaxWeaponNameLength);
                if (weaponName.Length == 0) continue;
                WeaponRecord record = into.Weapon(weaponName);
                record.Kills = ReadLong(weapon.Value, "kills", 0, CareerStats.MaxCounter);
                record.Deaths = ReadLong(weapon.Value, "deaths", 0, CareerStats.MaxCounter);
            }
        }

        if (stats.TryGetProperty("medals", out JsonElement medals) && medals.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty medal in medals.EnumerateObject())
            {
                if (!MedalCatalog.TryParseKey(medal.Name, out Medal id)) continue; // a newer build's medal
                into.SetMedalCount(id, ReadNumber(medal.Value, 0, CareerStats.MaxCounter));
            }
        }
    }

    // ---- small readers ------------------------------------------------------------------------

    private static string ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadLong(JsonElement parent, string name, long min, long max) =>
        parent.TryGetProperty(name, out JsonElement value) ? ReadNumber(value, min, max) : min;

    private static long ReadNumber(JsonElement value, long min, long max)
    {
        if (value.ValueKind != JsonValueKind.Number) return min;
        double number = value.TryGetInt64(out long whole) ? whole : value.GetDouble();
        if (double.IsNaN(number)) return min;
        if (number < min) return min;
        if (number > max) return max;
        return (long)number;
    }

    private static double ReadDouble(JsonElement parent, string name, double min, double max)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number) return min;
        double number = value.GetDouble();
        if (double.IsNaN(number) || double.IsInfinity(number)) return min;
        return Math.Min(Math.Max(number, min), max);
    }

    /// <summary>Ids are only ever compared, never displayed: letters, digits and dashes, at most 40.</summary>
    private static string SanitizeId(string raw)
    {
        var builder = new StringBuilder(Math.Min(raw.Length, 40));
        foreach (char c in raw)
        {
            if (builder.Length >= 40) break;
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-')
                builder.Append(c);
        }
        return builder.ToString();
    }
}
