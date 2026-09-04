namespace HKFBX.Model;

/// <summary>Something the animation announces at a moment: a footstep, a hit, the end of a clip.</summary>
public readonly record struct AnimationEvent(float Time, string Text);

/// <summary>
/// A named list of events. Havok calls this an annotation track, and the name is
/// kept because that is what the .hkx side of the conversion reads and writes.
/// </summary>
/// <remarks>
/// An animation may carry one track per transform track, though in practice
/// events are usually all on the first. Nothing here requires that: a track is
/// a name and a list of moments, and any number of them round trip.
/// </remarks>
public sealed class AnnotationTrack
{
    public required string Name { get; init; }

    public required IReadOnlyList<AnimationEvent> Events { get; init; }

    public override string ToString() => $"{Name}: {Events.Count} events";
}
