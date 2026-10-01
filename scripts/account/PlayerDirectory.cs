using System;
using System.Collections.Generic;

/// <summary>
/// Who the human players in the current session are: peer id -> their PlayerCard (account name,
/// avatar and stats). Pure C# and static, same idea as BotRoster (which does the same job for bots),
/// and BotRoster.DisplayName now asks this class for a person's name - so the kill feed, scoreboard,
/// results screen and name tags all show account names without any of them changing.
///
/// Filled by CareerTracker: the local player's own card the moment the session starts, and every
/// other player's card as they announce it over the network. Emptied (except "me") when the
/// session ends.
///
/// Version goes up on every change; UI that shows names (the floating name tag, the PLAYERS list)
/// compares it to know when to redraw instead of polling every card.
/// </summary>
public static class PlayerDirectory
{
    private static readonly Dictionary<long, PlayerCard> Remote = new();

    /// <summary>This machine's peer id (1 for the host, solo play and the editor).</summary>
    public static long LocalPeerId { get; private set; } = 1;

    /// <summary>The local player's card - the live PlayerAccount object, so their own stats are
    /// never stale.</summary>
    public static PlayerCard? LocalCard { get; private set; }

    public static int Version { get; private set; }

    public static void SetLocal(PlayerCard card)
    {
        LocalCard = card;
        Version++;
    }

    public static void SetLocalPeer(long peerId)
    {
        if (peerId == LocalPeerId) return;
        LocalPeerId = peerId;
        Version++;
    }

    /// <summary>Stores (or replaces) another player's card. Never overwrites the local player.</summary>
    public static void Set(long peerId, PlayerCard card)
    {
        if (peerId == LocalPeerId) return;
        Remote[peerId] = card;
        Version++;
    }

    public static void Remove(long peerId)
    {
        if (Remote.Remove(peerId)) Version++;
    }

    /// <summary>Forget everyone else; keep "me". Called when a session ends.</summary>
    public static void ClearRemote()
    {
        if (Remote.Count == 0) return;
        Remote.Clear();
        Version++;
    }

    /// <summary>Call after the local account changes (name or avatar) so name tags redraw.</summary>
    public static void Touch() => Version++;

    public static bool TryGet(long peerId, out PlayerCard card)
    {
        if (peerId == LocalPeerId && LocalCard != null) { card = LocalCard; return true; }
        return Remote.TryGetValue(peerId, out card);
    }

    /// <summary>
    /// The name to show for a human player, or null if this id is not a known person (a bot, or
    /// somebody whose card has not arrived yet - the caller falls back to "Player 12345").
    ///
    /// Two people can choose the same name. The one with the lower peer id keeps it and the next
    /// gets " (2)", and so on, so the scoreboard and kill feed never show two identical rows.
    /// </summary>
    public static string? NameOf(long peerId)
    {
        if (!TryGet(peerId, out PlayerCard card)) return null;

        int earlierWithSameName = 0;
        if (LocalCard != null && LocalPeerId < peerId && SameName(LocalCard, card)) earlierWithSameName++;
        foreach (KeyValuePair<long, PlayerCard> other in Remote)
            if (other.Key < peerId && other.Key != LocalPeerId && SameName(other.Value, card)) earlierWithSameName++;

        return earlierWithSameName == 0 ? card.Name : $"{card.Name} ({earlierWithSameName + 1})";
    }

    private static bool SameName(PlayerCard a, PlayerCard b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
}
