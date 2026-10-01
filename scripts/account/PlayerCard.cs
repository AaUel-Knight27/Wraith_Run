using System;
using System.Text;

/// <summary>
/// Who a player is and how they have done: the account name they chose, their avatar, and their
/// lifetime record. One instance is the local player's saved account; copies of it (minus the
/// local-only fields) are what players swap over the LAN so everyone can open everyone's stat card.
/// Pure C#.
/// </summary>
public sealed class PlayerCard
{
    /// <summary>Random, stable per install. Lets a card be recognised across reconnects even if the
    /// name changes. Never shown.</summary>
    public string AccountId = string.Empty;

    public string Name = "Player";

    /// <summary>Index into the emblem list (see AvatarCatalog.EmblemCount).</summary>
    public int AvatarEmblem;

    /// <summary>Index into the colour list (see AvatarCatalog.ColorCount).</summary>
    public int AvatarColor;

    /// <summary>Unix seconds when the account was first created.</summary>
    public long CreatedUnix;

    /// <summary>Local-only: has the player been through the create-account screen yet? Saved to
    /// disk, never sent to other players.</summary>
    public bool SetupDone;

    public CareerStats Stats = new CareerStats();
}

/// <summary>Sizes of the avatar palette. The actual icons and colours live in AvatarView (UI); this
/// is here so the pure layer can clamp an avatar it read from a file or a peer.</summary>
public static class AvatarCatalog
{
    public const int EmblemCount = 10;
    public const int ColorCount = 8;

    public static int ClampEmblem(int value) => value < 0 || value >= EmblemCount ? 0 : value;
    public static int ClampColor(int value) => value < 0 || value >= ColorCount ? 0 : value;
}

/// <summary>The rules for an account name: anything the player likes, within reason.</summary>
public static class AccountRules
{
    public const int MaxNameLength = 20;
    public const string FallbackName = "Player";

    /// <summary>
    /// Cleans a name for display and for the wire. Keeps every script and emoji (a player may well
    /// be typing Amharic, Arabic or Chinese), but drops what could break a layout or impersonate:
    /// control characters, invisible/formatting characters (including the right-to-left overrides
    /// that can reverse a name on screen), and line breaks. Runs of whitespace collapse to one
    /// space, the ends are trimmed, and the length is counted in whole characters (an emoji is one)
    /// so it is never cut in half. Returns an empty string if nothing usable is left.
    /// </summary>
    public static string SanitizeName(string? raw, int maxLength = MaxNameLength)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var builder = new StringBuilder(Math.Min(raw!.Length, maxLength * 2));
        int runes = 0;
        bool pendingSpace = false;

        foreach (Rune rune in raw.EnumerateRunes())
        {
            if (rune.Value == 0xFFFD) continue; // the replacement character: a broken encoding, not a name

            switch (Rune.GetUnicodeCategory(rune))
            {
                case System.Globalization.UnicodeCategory.Control:
                case System.Globalization.UnicodeCategory.Format:
                case System.Globalization.UnicodeCategory.LineSeparator:
                case System.Globalization.UnicodeCategory.ParagraphSeparator:
                case System.Globalization.UnicodeCategory.PrivateUse:
                case System.Globalization.UnicodeCategory.OtherNotAssigned:
                case System.Globalization.UnicodeCategory.Surrogate:
                    // A tab or newline is still a gap between words; everything else is dropped.
                    if (rune.Value == '\t' || rune.Value == '\n' || rune.Value == '\r') pendingSpace = true;
                    continue;
                case System.Globalization.UnicodeCategory.SpaceSeparator:
                    pendingSpace = true;
                    continue;
            }

            if (runes >= maxLength) break;
            if (pendingSpace && builder.Length > 0)
            {
                if (runes + 1 >= maxLength) break; // no room for the space AND a character after it
                builder.Append(' ');
                runes++;
            }
            pendingSpace = false;
            builder.Append(rune.ToString());
            runes++;
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>A name for a brand-new account before the player has typed one: "Operator4821".</summary>
    public static string RandomName(Random rng) => "Operator" + rng.Next(1000, 10000).ToString();
}
