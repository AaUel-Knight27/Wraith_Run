using Godot;

/// <summary>Base for the class-ability mechanics. Lives under the session's per-drill Props node, so it is
/// freed automatically when the drill ends. Gives them the shared prompt / progress-bar plumbing.</summary>
public abstract partial class TrainingMechanic : Node
{
    protected TrainingSession Session => TrainingSession.Instance!;

    private bool _prompting;
    private bool _channeling;

    protected void ShowPrompt(string? text)
    {
        if (TrainingSession.Instance == null) return;
        if (text == null)
        {
            if (_prompting) Session.PromptText = string.Empty;
            _prompting = false;
            return;
        }
        Session.PromptText = text;
        _prompting = true;
    }

    protected void ShowChannel(float progress, string label)
    {
        if (TrainingSession.Instance == null) return;
        Session.ChannelProgress = progress;
        Session.ChannelLabel = label;
        _channeling = true;
    }

    protected void HideChannel()
    {
        if (TrainingSession.Instance == null) return;
        if (_channeling)
        {
            Session.ChannelProgress = -1.0f;
            Session.ChannelLabel = string.Empty;
        }
        _channeling = false;
    }

    public override void _ExitTree()
    {
        ShowPrompt(null);
        HideChannel();
    }

    protected static float HorizontalSpeed(CharacterBody3D body) => new Vector2(body.Velocity.X, body.Velocity.Z).Length();
}
