/// <summary>True while the hub (the walkable warehouse home base) is the running world. Combat rules
/// that only make sense in a match - damage, mainly - look at this instead of finding the scene.</summary>
public static class HubState
{
    public static bool Active;
}
