namespace HKFBX.Fbx;

/// <summary>
/// How bone names are spelled in the FBX.
/// </summary>
public enum BoneNaming
{
    /// <summary>
    /// Exactly as Havok has them, brackets and spaces included, such as
    /// <c>NPC Root [Root]</c>.
    /// </summary>
    Havok,

    /// <summary>
    /// ck-cmd's escaping, which some tools need because they will not keep a
    /// name containing a space, a bracket or a colon.
    /// </summary>
    CkCmd,
}

/// <summary>
/// ck-cmd's bone name escaping.
/// </summary>
/// <remarks>
/// Skyrim's bone names carry characters that several 3D applications will not
/// round trip — <c>NPC L Finger02 [LF02]</c> has two of them — so ck-cmd
/// substitutes each for a marker. Reading undoes it unconditionally: a name that
/// carries no marker is returned unchanged, so a file from either convention
/// comes back with the names Havok expects.
/// </remarks>
public static class BoneNames
{
    // Longest first is not needed here since the markers share no prefixes, but
    // the pairs must be applied consistently in both directions.
    private static readonly (string Character, string Marker)[] Escapes =
    [
        (" ", "_s_"),
        ("[", "_ob_"),
        ("]", "_cb_"),
        (":", "_dd_"),
    ];

    /// <summary>Havok's spelling to ck-cmd's.</summary>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach ((string character, string marker) in Escapes)
            name = name.Replace(character, marker, StringComparison.Ordinal);

        return name;
    }

    /// <summary>ck-cmd's spelling back to Havok's.</summary>
    public static string Unsanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach ((string character, string marker) in Escapes)
            name = name.Replace(marker, character, StringComparison.Ordinal);

        return name;
    }

    /// <summary>Applies a naming convention to a bone name.</summary>
    public static string Apply(string name, BoneNaming naming) =>
        naming == BoneNaming.CkCmd ? Sanitize(name) : name;
}
