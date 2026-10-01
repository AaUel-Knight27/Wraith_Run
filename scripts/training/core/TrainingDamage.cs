using Godot;

/// <summary>
/// The one place training damage reaches the trainee.
///
/// It writes Health.CurrentHealth directly instead of Health.ReceiveDamage because (a) ReceiveDamage is
/// an RPC that is dropped when caller and target are the same peer, and (b) a training player must never
/// actually die/respawn at a random map point - being "downed" ends the round instead. CurrentHealth is
/// the property PlayerAudio/PlayerHud already watch, so hit sounds, the low-health breathing loop and the
/// health bar all react exactly as in a match.
/// </summary>
public static class TrainingDamage
{
    /// <summary>Damage the trainee. Never drops below 1 HP; reaching it reports "downed" to the session.</summary>
    public static void HitTrainee(Health health, float amount)
    {
        if (health.IsDead || amount <= 0.0f) return;
        float remaining = health.CurrentHealth - amount;
        bool downed = remaining <= 1.0f;
        health.CurrentHealth = downed ? 1.0f : remaining;
        TrainingSession.Instance?.NotifyTraineeDamaged(amount, downed);
    }

    /// <summary>Puts the trainee at a fraction of max HP (verge-of-death practice, heal-self practice).</summary>
    public static void SetTraineeHealthFraction(Health health, float fraction)
    {
        health.CurrentHealth = Mathf.Max(1.0f, health.MaxHealth * Mathf.Clamp(fraction, 0.0f, 1.0f));
    }

    public static void FullHealTrainee(Health health)
    {
        health.CurrentHealth = health.MaxHealth;
    }
}
