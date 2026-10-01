using Godot;
using System;
using System.Collections.Generic;

/// <summary>Every callout the training center understands. Add a value + a row in CalloutInfo and it appears in the V menu.</summary>
public enum CalloutType
{
    NeedMedic, OnMyWay, Healing, CoverMe, EnemySpotted, Clear, HoldPosition, MoveIn,
    FireInTheHole, ChargePlanted, Detonating, NeedRepair, Repairing, SlowDown, GoGo, Ready, Clearing,
}

public sealed class CalloutMessage
{
    public CalloutType Type;
    public string Sender = "";
    public bool FromPlayer;
    public bool HasPosition;
    public Vector3 Position;
    public float Time;            // seconds since the session started
    public long SenderPeerId;     // real wiring: the sending peer
}

public static class CalloutInfo
{
    public static string Text(CalloutType type) => type switch
    {
        CalloutType.NeedMedic => "MEDIC!",
        CalloutType.OnMyWay => "On my way",
        CalloutType.Healing => "Healing you",
        CalloutType.CoverMe => "Cover me",
        CalloutType.EnemySpotted => "Enemy spotted",
        CalloutType.Clear => "Clear",
        CalloutType.HoldPosition => "Hold position",
        CalloutType.MoveIn => "Move in!",
        CalloutType.FireInTheHole => "FIRE IN THE HOLE!",
        CalloutType.ChargePlanted => "Charge planted",
        CalloutType.Detonating => "Detonating!",
        CalloutType.NeedRepair => "Need repairs",
        CalloutType.Repairing => "Repairing",
        CalloutType.SlowDown => "Slow down / stop",
        CalloutType.GoGo => "Go go go",
        CalloutType.Ready => "Ready",
        CalloutType.Clearing => "Clearing the blast zone",
        _ => type.ToString(),
    };

    public static Color ColorOf(CalloutType type) => type switch
    {
        CalloutType.NeedMedic => new Color(1.0f, 0.4f, 0.4f),
        CalloutType.FireInTheHole => new Color(1.0f, 0.6f, 0.2f),
        CalloutType.Detonating => new Color(1.0f, 0.6f, 0.2f),
        CalloutType.EnemySpotted => new Color(1.0f, 0.85f, 0.3f),
        CalloutType.Healing => new Color(0.5f, 1.0f, 0.6f),
        CalloutType.OnMyWay => new Color(0.5f, 1.0f, 0.6f),
        CalloutType.Clear => new Color(0.5f, 1.0f, 0.6f),
        CalloutType.Clearing => new Color(0.5f, 1.0f, 0.6f),
        _ => new Color(0.75f, 0.9f, 1.0f),
    };

    /// <summary>Order shown in the V menu.</summary>
    public static readonly CalloutType[] MenuOrder =
    {
        CalloutType.EnemySpotted, CalloutType.CoverMe, CalloutType.HoldPosition, CalloutType.MoveIn,
        CalloutType.NeedMedic, CalloutType.OnMyWay, CalloutType.Healing,
        CalloutType.ChargePlanted, CalloutType.FireInTheHole, CalloutType.Detonating, CalloutType.Clear,
        CalloutType.NeedRepair, CalloutType.Repairing, CalloutType.SlowDown, CalloutType.GoGo, CalloutType.Ready,
    };
}

/// <summary>Training transport: everyone is on this peer, so "broadcast" is a direct call. The real game
/// implements ICalloutTransport over an RPC and assigns it to CalloutSystem.Transport - nothing else changes.</summary>
public sealed class LocalCalloutTransport : ICalloutTransport
{
    public event Action<CalloutMessage>? Received;
    public void Broadcast(CalloutMessage message) => Received?.Invoke(message);
}

/// <summary>
/// Comms. Anyone (the trainee, a friendly Manny, a drill) calls Send(); every listener - friend AI, drills,
/// the HUD log - subscribes to Issued. World markers (floating text + a ping ring) are drawn here.
/// </summary>
public partial class CalloutSystem : Node3D
{
    public event Action<CalloutMessage>? Issued;

    public readonly List<CalloutMessage> Recent = new();
    private ICalloutTransport _transport = new LocalCalloutTransport();
    private readonly List<(Node3D node, float expire)> _markers = new();
    private float _clock;

    public ICalloutTransport Transport
    {
        get => _transport;
        set
        {
            _transport.Received -= OnReceived;
            _transport = value;
            _transport.Received += OnReceived;
        }
    }

    public float Clock => _clock;

    public override void _Ready()
    {
        _transport.Received += OnReceived;
    }

    public override void _ExitTree()
    {
        _transport.Received -= OnReceived;
    }

    public void Send(CalloutType type, string sender, bool fromPlayer, Vector3? position = null)
    {
        _transport.Broadcast(new CalloutMessage
        {
            Type = type,
            Sender = sender,
            FromPlayer = fromPlayer,
            HasPosition = position.HasValue,
            Position = position ?? Vector3.Zero,
            Time = _clock,
        });
    }

    /// <summary>Most recent callout of this type (or null), optionally only if it is newer than maxAgeSeconds.</summary>
    public CalloutMessage? Latest(CalloutType type, float maxAgeSeconds = float.MaxValue)
    {
        for (int i = Recent.Count - 1; i >= 0; i--)
        {
            if (Recent[i].Type != type) continue;
            return _clock - Recent[i].Time <= maxAgeSeconds ? Recent[i] : null;
        }
        return null;
    }

    private void OnReceived(CalloutMessage message)
    {
        Recent.Add(message);
        if (Recent.Count > 24) Recent.RemoveAt(0);
        SpawnMarker(message);
        Issued?.Invoke(message);
    }

    private void SpawnMarker(CalloutMessage message)
    {
        // No world position: nothing to draw in the world; the HUD log still shows it.
        if (!message.HasPosition) return;

        Color color = CalloutInfo.ColorOf(message.Type);
        var marker = new Node3D { Name = "CalloutMarker" };
        AddChild(marker);
        marker.GlobalPosition = message.Position + new Vector3(0, 2.2f, 0);
        marker.AddChild(TrainingUtil.Label($"{message.Sender}: {CalloutInfo.Text(message.Type)}", Vector3.Zero, color, 36, true));
        marker.AddChild(TrainingUtil.GroundDisc(0.7f, color, new Vector3(0, -2.2f, 0), 0.45f));
        _markers.Add((marker, _clock + 3.5f));
    }

    public override void _Process(double delta)
    {
        _clock += (float)delta;
        for (int i = _markers.Count - 1; i >= 0; i--)
        {
            if (_clock < _markers[i].expire) continue;
            TrainingUtil.SafeFree(_markers[i].node);
            _markers.RemoveAt(i);
        }
    }
}
