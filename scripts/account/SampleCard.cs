/// <summary>
/// A fully filled-in example card ("Ash", the numbers from the original stat-card design) for
/// previewing the UI without playing 645 matches. Never saved and never sent to anyone; the PROFILE
/// screen offers it only in debug builds.
/// </summary>
public static class SampleCard
{
    public static PlayerCard Ash()
    {
        var card = new PlayerCard
        {
            AccountId = "sample-ash",
            Name = "Ash",
            AvatarEmblem = 0,
            AvatarColor = 5,
        };

        CareerStats s = card.Stats;
        s.Wins = 409;
        s.Ties = 2;
        s.Losses = 234;
        s.Kills = 14673;
        s.Deaths = 6690;
        s.PlaytimeSeconds = 64.9 * 3600.0;

        void Weapon(string name, long kills, long deaths)
        {
            WeaponRecord record = s.Weapon(name);
            record.Kills = kills;
            record.Deaths = deaths;
        }
        Weapon("AK-47", 49, 6);          // K/D 8.17
        Weapon("M4", 228, 37);           // 6.16
        Weapon("Desert Eagle", 59, 12);  // 4.92
        Weapon("P90", 1057, 230);        // 4.60

        s.SetMedalCount(Medal.ClearKill, 10338);
        s.SetMedalCount(Medal.HeadShot, 2090);
        s.SetMedalCount(Medal.Revenge, 1324);
        s.SetMedalCount(Medal.MajorStreak, 162);
        s.SetMedalCount(Medal.StopStreak, 2323);
        s.SetMedalCount(Medal.Explosive, 179);
        s.SetMedalCount(Medal.Knife, 955);
        s.SetMedalCount(Medal.Assist, 1114);
        s.SetMedalCount(Medal.DoubleKill, 2990);
        s.SetMedalCount(Medal.TripleKill, 1115);
        s.SetMedalCount(Medal.KingSlayer, 205);
        s.SetMedalCount(Medal.OneShotMultiKill, 98);
        s.SetMedalCount(Medal.WallBang, 171);
        s.SetMedalCount(Medal.NoScope360, 190);
        s.SetMedalCount(Medal.LastBullet, 658);
        s.SetMedalCount(Medal.OnVergeOfDeath, 793);
        s.SetMedalCount(Medal.TeamWipe, 235);
        s.SetMedalCount(Medal.FirstBlood, 178);
        s.SetMedalCount(Medal.OneShotOneKill, 5367);
        s.SetMedalCount(Medal.CloseCombat, 951);
        s.SetMedalCount(Medal.LongShot, 847);
        s.SetMedalCount(Medal.HipFire, 7484);
        return card;
    }
}
