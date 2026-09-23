namespace HKFBX.Model;

/// <summary>What renaming a rig's bones changed, and what it could not.</summary>
/// <param name="Rig">The rig under its new names.</param>
/// <param name="Renamed">How many bones took a new name.</param>
/// <param name="Unmapped">
/// The bones the map said nothing about, which keep the names they had. A bone nothing
/// else looks for may keep its name safely; one a ragdoll or a skin looks for may not.
/// </param>
public sealed record RenamedSkeleton(Skeleton Rig, int Renamed, IReadOnlyList<string> Unmapped)
{
    public override string ToString() =>
        $"{Renamed} bones renamed, {Unmapped.Count} left as they were";
}

/// <summary>
/// Puts a rig's bones under the names something else is looking for.
/// </summary>
/// <remarks>
/// <para>
/// A Havok skeleton packfile is never written from nothing: it carries a ragdoll, the
/// constraints between its bodies and the two mappings between rig and ragdoll, and all
/// of that is cloned from a creature that already has one. The clone is matched up
/// <em>by bone name</em>, so a rig whose bones are called <c>Bip01_L_Thigh</c> takes
/// nothing at all from a prototype whose bones are called <c>NPC L Thigh [LThg]</c>,
/// however alike the two creatures are.
/// </para>
/// <para>
/// Renaming is therefore the bridge between an asset and a prototype, and it is worth
/// doing rather than avoiding: a humanoid's bones do the same jobs whatever an exporter
/// called them, and the game's own names are what every other part of Skyrim looks for.
/// The bones a map says nothing about keep the names they had and are reported, since
/// whether that matters depends on who else is looking for them.
/// </para>
/// </remarks>
public static class SkeletonNaming
{
    /// <summary>Renames what the map names, keeping every bone's place in the rig.</summary>
    /// <param name="rig">The rig to rename.</param>
    /// <param name="names">Old name to new, matched without regard to case.</param>
    public static RenamedSkeleton Rename(Skeleton rig, IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(names);

        var map = new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);
        var bones = new List<Bone>(rig.Count);
        var unmapped = new List<string>();
        int renamed = 0;

        foreach (Bone bone in rig.Bones)
        {
            if (map.TryGetValue(bone.Name, out string? now) && !string.Equals(now, bone.Name, StringComparison.Ordinal))
            {
                bones.Add(bone with { Name = now });
                renamed++;
            }
            else
            {
                bones.Add(bone);
                if (!map.ContainsKey(bone.Name)) unmapped.Add(bone.Name);
            }
        }

        // Two bones under one name is a rig nothing can be matched against: whichever
        // was looked for, the wrong one may answer.
        var clashes = bones.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (clashes.Count > 0)
            throw new ArgumentException(
                $"the map would give {clashes.Count} names to more than one bone each: {string.Join(", ", clashes.Take(5))}",
                nameof(names));

        return new RenamedSkeleton(new Skeleton { Name = rig.Name, Bones = bones }, renamed, unmapped);
    }

    /// <summary>
    /// The 3ds Max biped naming, under the names Skyrim's own humanoid rig uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two are the same rig. Bethesda built Skyrim's humanoid on a Max biped and kept
    /// its joints, so every bone here pairs with exactly one bone there, and the pairing is
    /// a matter of record rather than of judgement: the biped's <c>Spine</c>, <c>Spine1</c>,
    /// <c>Spine2</c> are the game's <c>Spn0</c>, <c>Spn1</c>, <c>Spn2</c>, and its fingers
    /// count from <c>Finger0</c> where the game's count from <c>Finger00</c>.
    /// </para>
    /// <para>
    /// The biped's own <c>Bip01</c> sits at the hips and is the game's <c>NPC COM</c>, not
    /// its <c>NPC Root</c>: the game's root is a separate bone on the ground under the
    /// creature, which a biped export has not got and <see cref="SkeletonGrounding"/> adds.
    /// </para>
    /// <para>
    /// The spine is not here, because it cannot be settled by spelling: a biped's spine runs
    /// to as many links as its author asked for, and which of them is the one the game calls
    /// the neck depends on how many there are. <see cref="BipedMap"/> reads it off the rig.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Biped { get; } = BuildBiped();

    private static Dictionary<string, string> BuildBiped()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01"] = "NPC COM [COM ]",
            ["Bip01_Pelvis"] = "NPC Pelvis [Pelv]",
        };

        foreach ((string side, string tag) in new[] { ("L", "L"), ("R", "R") })
        {
            map[$"Bip01_{side}_Thigh"] = $"NPC {tag} Thigh [{tag}Thg]";
            map[$"Bip01_{side}_Calf"] = $"NPC {tag} Calf [{tag}Clf]";
            // The game pads the foot's short tag to four characters; the others do not.
            map[$"Bip01_{side}_Foot"] = $"NPC {tag} Foot [{tag}ft ]";
            map[$"Bip01_{side}_Toe0"] = $"NPC {tag} Toe0 [{tag}Toe]";
            map[$"Bip01_{side}_Clavicle"] = $"NPC {tag} Clavicle [{tag}Clv]";
            map[$"Bip01_{side}_UpperArm"] = $"NPC {tag} UpperArm [{tag}Uar]";
            map[$"Bip01_{side}_Forearm"] = $"NPC {tag} Forearm [{tag}Lar]";
            map[$"Bip01_{side}_Hand"] = $"NPC {tag} Hand [{tag}Hnd]";

            // A biped numbers a finger's first joint bare and the rest with a digit after it:
            // Finger0, Finger01, Finger02. The game numbers all three: Finger00 and on.
            for (int finger = 0; finger < 5; finger++)
            {
                map[$"Bip01_{side}_Finger{finger}"] = $"NPC {tag} Finger{finger}0 [{tag}F{finger}0]";
                for (int joint = 1; joint <= 2; joint++)
                    map[$"Bip01_{side}_Finger{finger}{joint}"] = $"NPC {tag} Finger{finger}{joint} [{tag}F{finger}{joint}]";
            }
        }

        return map;
    }

    /// <summary>
    /// The biped map for one particular rig, with its spine read off the rig itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The limbs and the fingers come from <see cref="Biped"/>, which knows them by name.
    /// The chain from the pelvis to the head does not, because a biped's spine has as many
    /// links as its author asked for and the game's has three: whether a rig's
    /// <c>Bip01_Spine4</c> is the game's second spine link or its neck is a question about
    /// that rig, not about the naming.
    /// </para>
    /// <para>
    /// So the chain is walked from the pelvis upwards and the game's names are laid along
    /// it from both ends: the three spine links from the bottom, and the head, the neck and
    /// the magic node from the top, since the head is what the last link is whatever it is
    /// called. A chain with more links than names leaves the surplus in the middle named as
    /// it was, where an extra spine joint does least harm; a chain with fewer takes the
    /// names that reach.
    /// </para>
    /// </remarks>
    /// <param name="rig">The rig whose chain to read.</param>
    /// <param name="hasMagicNode">
    /// Whether the topmost bone is a magic node rather than the head. A biped that ends
    /// <c>Head1, Head2</c> has one; one that ends at a single head has not.
    /// </param>
    public static IReadOnlyDictionary<string, string> BipedMap(Skeleton rig, bool hasMagicNode = true)
    {
        ArgumentNullException.ThrowIfNull(rig);

        var map = new Dictionary<string, string>(Biped, StringComparer.OrdinalIgnoreCase);
        var chain = Chain(rig);
        if (chain.Count == 0) return map;

        // Which link is the game's Spine2 is not a matter of counting, because a biped's spine
        // is as long as its author wanted. It is a matter of where the arms are: in Skyrim the
        // clavicles hang off NPC Spine2 [Spn2] and nothing else, so whichever link this rig
        // hangs its arms from is that bone, whatever the exporter numbered it.
        int shoulder = chain.FindIndex(link => Bears(rig, link));

        // Failing that -- a rig with no arms, or arms somewhere unexpected -- the three spine
        // links are counted up from the bottom, which is right whenever the spine is the
        // length the game's is.
        if (shoulder < 0) shoulder = Math.Min(2, chain.Count - 1);

        string[] below = ["NPC Spine2 [Spn2]", "NPC Spine1 [Spn1]", "NPC Spine [Spn0]"];
        for (int i = 0; i < below.Length && shoulder - i >= 0; i++)
            map[chain[shoulder - i].Name] = below[i];

        // Above the shoulders the names are laid from the top down, since the last bone is the
        // head -- or the node above it -- whatever the exporter called it. A rig with more
        // links up there than names leaves the surplus named as it came.
        string[] above = hasMagicNode
            ? ["NPC Head MagicNode [Hmag]", "NPC Head [Head]", "NPC Neck [Neck]"]
            : ["NPC Head [Head]", "NPC Neck [Neck]"];

        for (int i = 0; i < above.Length && chain.Count - 1 - i > shoulder; i++)
            map[chain[chain.Count - 1 - i].Name] = above[i];

        return map;
    }

    /// <summary>Whether a bone carries an arm, which is what makes it the game's Spine2.</summary>
    private static bool Bears(Skeleton rig, Bone link)
    {
        int at = rig.Bones.ToList().FindIndex(b => ReferenceEquals(b, link));
        if (at < 0) return false;

        for (int i = 0; i < rig.Count; i++)
        {
            if (rig.Bones[i].ParentIndex != at) continue;
            string name = rig.Bones[i].Name;
            if (name.Contains("Clavicle", StringComparison.OrdinalIgnoreCase)
                || name.Contains("UpperArm", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// The single line of bones from the hips up to the topmost bone above them.
    /// </summary>
    /// <remarks>
    /// It starts at the biped's own root rather than at the pelvis, because a 3ds Max biped
    /// does not run the spine through the pelvis: <c>Bip01_Pelvis</c> and <c>Bip01_Spine</c>
    /// are both children of <c>Bip01</c>, the pelvis carrying the legs and the spine carrying
    /// everything else. Walking up from the pelvis finds two thighs and stops.
    /// </remarks>
    private static List<Bone> Chain(Skeleton rig)
    {
        var chain = new List<Bone>();
        int at = Index(rig, "Bip01");
        if (at < 0) at = Index(rig, "NPC COM [COM ]");
        if (at < 0) at = Index(rig, "Bip01_Pelvis");
        if (at < 0) at = Index(rig, "NPC Pelvis [Pelv]");
        if (at < 0) return chain;

        while (true)
        {
            var up = new List<int>();
            for (int i = 0; i < rig.Count; i++)
                if (rig.Bones[i].ParentIndex == at && !IsLimb(rig.Bones[i].Name)) up.Add(i);

            if (up.Count != 1) break;

            at = up[0];
            chain.Add(rig.Bones[at]);
        }

        return chain;
    }

    private static bool IsLimb(string name) =>
        name.Contains("Pelvis", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Thigh", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Clavicle", StringComparison.OrdinalIgnoreCase)
        || name.Contains("UpperArm", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Pauldron", StringComparison.OrdinalIgnoreCase);

    private static int Index(Skeleton rig, string name)
    {
        for (int i = 0; i < rig.Count; i++)
            if (string.Equals(rig.Bones[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// A map from one rig's bones to another's, worked out by where the bones stand.
    /// </summary>
    /// <remarks>
    /// Offered as a starting point for a person to correct, not as an answer. Two
    /// humanoids have the same bones doing the same jobs, but which of a hand's fingers
    /// is which cannot be told from a rest pose, and a rig with a bone the other has not
    /// got will pair it with whatever is nearest.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Suggest(Skeleton from, Skeleton to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var suggestion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (from.Count == 0 || to.Count == 0) return suggestion;

        // Both rigs are read in their own scale, so a tall creature and a short one are
        // compared by proportion rather than by units.
        System.Numerics.Vector3[] here = Places(from), there = Places(to);
        float mine = here.Max(p => p.Z), theirs = there.Max(p => p.Z);
        if (mine <= 0 || theirs <= 0) return suggestion;

        var taken = new HashSet<int>();
        for (int i = 0; i < from.Count; i++)
        {
            System.Numerics.Vector3 a = here[i] / mine;
            int best = -1;
            float nearest = float.MaxValue;

            for (int j = 0; j < to.Count; j++)
            {
                if (taken.Contains(j)) continue;
                float distance = (there[j] / theirs - a).LengthSquared();
                if (distance >= nearest) continue;
                nearest = distance;
                best = j;
            }

            if (best < 0) continue;
            suggestion[from.Bones[i].Name] = to.Bones[best].Name;
            taken.Add(best);
        }

        return suggestion;
    }

    private static System.Numerics.Vector3[] Places(Skeleton rig)
    {
        var world = new System.Numerics.Matrix4x4[rig.Count];
        var places = new System.Numerics.Vector3[rig.Count];

        for (int i = 0; i < rig.Count; i++)
        {
            BoneTransform rest = rig.Bones[i].ReferencePose;
            System.Numerics.Matrix4x4 local =
                System.Numerics.Matrix4x4.CreateScale(rest.Scale)
                * System.Numerics.Matrix4x4.CreateFromQuaternion(rest.Rotation)
                * System.Numerics.Matrix4x4.CreateTranslation(rest.Translation);

            int parent = rig.Bones[i].ParentIndex;
            world[i] = parent < 0 ? local : local * world[parent];
            places[i] = world[i].Translation;
        }

        return places;
    }
}
