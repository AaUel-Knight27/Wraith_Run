using Godot;
using System;

/// <summary>
/// The local player's account: one name, one avatar, one lifetime record, saved to
/// user://account.json. A plain static class (same pattern as PlayerProfile, which keeps the
/// operator/loadout in its own file) - nothing here needs _Process or signals; the node that does
/// (CareerTracker) calls in.
///
/// Saving is built so a crash or a power cut cannot lose a career:
///   1. the new JSON is written to account.json.tmp first,
///   2. the last good account.json is copied to account.json.bak,
///   3. the .tmp is renamed over account.json.
/// Loading tries account.json, then account.json.bak. If BOTH are unreadable the damaged file is
/// kept as account.corrupt.json (never silently overwritten) and a fresh account starts.
/// </summary>
public static class PlayerAccount
{
    private const string FilePath = "user://account.json";
    private const string TempPath = "user://account.json.tmp";
    private const string BackupPath = "user://account.json.bak";
    private const string CorruptPath = "user://account.corrupt.json";

    private static PlayerCard? _current;

    /// <summary>The account. Loaded from disk on first use; a brand-new install gets a default one
    /// (random name, SetupDone = false) so stats can be recorded even before the player has visited
    /// the create-account screen.</summary>
    public static PlayerCard Current
    {
        get
        {
            if (_current == null) _current = LoadOrCreate();
            return _current;
        }
    }

    public static string Name => Current.Name;

    /// <summary>False until the player has been through the create-account screen once.</summary>
    public static bool SetupDone => Current.SetupDone;

    /// <summary>Saves the chosen name and avatar and marks setup as done. The name is sanitised; an
    /// empty result leaves the old name alone and returns false.</summary>
    public static bool Apply(string rawName, int emblem, int color)
    {
        string name = AccountRules.SanitizeName(rawName);
        if (name.Length == 0) return false;

        PlayerCard card = Current;
        card.Name = name;
        card.AvatarEmblem = AvatarCatalog.ClampEmblem(emblem);
        card.AvatarColor = AvatarCatalog.ClampColor(color);
        card.SetupDone = true;
        PlayerDirectory.Touch();
        Save();
        return true;
    }

    // -------------------------------------------------------------------------------------------
    // Save
    // -------------------------------------------------------------------------------------------

    public static bool Save()
    {
        string json = PlayerCardCodec.Encode(Current, forDisk: true);

        // 1. Write the complete new file beside the real one.
        using (FileAccess? temp = FileAccess.Open(TempPath, FileAccess.ModeFlags.Write))
        {
            if (temp == null)
            {
                GD.PushWarning($"PlayerAccount: could not write {TempPath}: {FileAccess.GetOpenError()}");
                return false;
            }
            temp.StoreString(json);
        } // disposed here = flushed and closed

        // 2. Keep the last good save as the backup.
        if (FileAccess.FileExists(FilePath))
            DirAccess.CopyAbsolute(FilePath, BackupPath);

        // 3. Swap. A rename is atomic on the platforms we ship to, so the real file is always either
        //    the old complete save or the new complete save, never half of each.
        Error error = DirAccess.RenameAbsolute(TempPath, FilePath);
        if (error == Error.Ok) return true;

        GD.PushWarning($"PlayerAccount: rename failed ({error}); writing {FilePath} directly.");
        using FileAccess? direct = FileAccess.Open(FilePath, FileAccess.ModeFlags.Write);
        if (direct == null) return false;
        direct.StoreString(json);
        return true;
    }

    // -------------------------------------------------------------------------------------------
    // Load
    // -------------------------------------------------------------------------------------------

    private static PlayerCard LoadOrCreate()
    {
        string? main = ReadText(FilePath);
        if (main != null && PlayerCardCodec.TryDecode(main, out PlayerCard? fromMain) && fromMain != null)
            return Finish(fromMain);

        string? backup = ReadText(BackupPath);
        if (backup != null && PlayerCardCodec.TryDecode(backup, out PlayerCard? fromBackup) && fromBackup != null)
        {
            GD.PushWarning("PlayerAccount: account.json was unreadable; restored from account.json.bak.");
            return Finish(fromBackup);
        }

        // A file exists but nothing could be read from it. Keep it for inspection/recovery rather
        // than letting the next Save() overwrite it.
        if (main != null && main.Length > 0)
        {
            GD.PushWarning($"PlayerAccount: {FilePath} is damaged and there is no usable backup; keeping it as {CorruptPath}.");
            DirAccess.CopyAbsolute(FilePath, CorruptPath);
        }
        return NewAccount();
    }

    /// <summary>Anything a loaded card may be missing (older save, hand-edited file).</summary>
    private static PlayerCard Finish(PlayerCard card)
    {
        if (card.AccountId.Length == 0) card.AccountId = Guid.NewGuid().ToString("N");
        if (card.CreatedUnix == 0) card.CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return card;
    }

    private static PlayerCard NewAccount()
    {
        var rng = new Random();
        var card = new PlayerCard
        {
            AccountId = Guid.NewGuid().ToString("N"),
            Name = AccountRules.RandomName(rng),
            AvatarEmblem = rng.Next(AvatarCatalog.EmblemCount),
            AvatarColor = rng.Next(AvatarCatalog.ColorCount),
            CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            SetupDone = false,
        };
        return card;
    }

    private static string? ReadText(string path)
    {
        if (!FileAccess.FileExists(path)) return null;
        using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        return file?.GetAsText();
    }
}
