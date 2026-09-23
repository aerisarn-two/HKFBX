using LeanMeshIO;

namespace HKFBX.Fbx;

/// <summary>What renaming a scene's bones changed, so it can be put back.</summary>
/// <param name="Renamed">The objects renamed, by id, each with the qualified name it had.</param>
public sealed record RenamedBones(IReadOnlyDictionary<long, string> Renamed)
{
    public int Count => Renamed.Count;

    public override string ToString() => $"{Renamed.Count} bones renamed";
}

/// <summary>
/// Renames a scene's bones in the document, so that whatever converts it next sees the new
/// names.
/// </summary>
/// <remarks>
/// <para>
/// Reading a rig out of a scene, correcting it and writing the correction somewhere else
/// leaves the scene as it was. That is fine while only the rig is wanted, and wrong as soon
/// as anything else is taken from the same file -- a skinned body, most obviously, whose skin
/// names the bones it is weighted to. Converted from the file as bought, a body comes out
/// weighted to <c>Bip01_L_Hand</c> on a creature whose skeleton calls that bone
/// <c>NPC L Hand [LHnd]</c>, and every vertex it moves is left at the origin.
/// </para>
/// <para>
/// A skinned mesh needs nothing else done to it, which is worth saying because it looks like
/// it should. Standing the creature on the floor, bringing it over the origin and turning it
/// to face forward are all carried by the skeleton: the skin's bind is taken in the pose the
/// file was authored in, and the bone's own transform is whatever the corrected skeleton says
/// it is now, so the mesh follows. Only the names have to change.
/// </para>
/// <para>
/// A cluster names its bone by object id and not by name, so nothing inside the scene breaks.
/// The names are what a converter reads, which is the point.
/// </para>
/// </remarks>
public static class FbxBoneRenaming
{
    /// <summary>Renames the bones <paramref name="names"/> names, and leaves the rest.</summary>
    /// <param name="document">The scene to rename in, which is changed.</param>
    /// <param name="names">Old bone name to new, matched without regard to case.</param>
    public static RenamedBones Rename(FbxDocument document, IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(names);

        var map = new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);
        var was = new Dictionary<long, string>();
        var scene = new FbxScene(document);

        foreach (FbxObject model in scene.Objects.Where(o => o.Class == "Model"))
        {
            // A scene spells a bone's name its own way -- an exporter replaces the characters
            // it cannot keep -- so it is read back the way everything else here reads it.
            string name = BoneNames.Unsanitize(model.Name);
            if (!map.TryGetValue(name, out string? now)) continue;
            if (string.Equals(name, now, StringComparison.Ordinal)) continue;

            was[model.Id] = model.QualifiedName;
            model.QualifiedName = $"Model::{BoneNames.Sanitize(now)}";
        }

        return new RenamedBones(was);
    }

    /// <summary>Puts the names back, for a document something else still wants unchanged.</summary>
    public static void Restore(FbxDocument document, RenamedBones renamed)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renamed);

        if (renamed.Count == 0) return;

        var scene = new FbxScene(document);
        foreach (FbxObject model in scene.Objects.Where(o => o.Class == "Model"))
            if (renamed.Renamed.TryGetValue(model.Id, out string? was)) model.QualifiedName = was;
    }
}
