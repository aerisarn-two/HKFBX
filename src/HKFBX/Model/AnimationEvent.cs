namespace HKFBX.Model;

/// <summary>Something the animation announces at a moment: a footstep, a hit, the end of a clip.</summary>
public readonly record struct AnimationEvent(float Time, string Text);

/// <summary>
/// A named list of events, which Havok calls an annotation track.
/// </summary>
/// <remarks>
/// An animation carries one track per transform track, though in practice only
/// the first holds anything: that is where Skyrim puts the events its behaviour
/// graphs listen for.
/// </remarks>
public sealed class AnnotationTrack
{
    public required string Name { get; init; }

    public required IReadOnlyList<AnimationEvent> Events { get; init; }

    public override string ToString() => $"{Name}: {Events.Count} events";
}
