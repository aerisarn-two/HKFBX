# HKFBX

Converts Havok animations to FBX and back. A component, not a tool: it is
consumed as a library.

## What it does

An `.hkx` animation is a set of spline-compressed curves indexed by track. An
FBX animation is a stack of per-component keys hung off a node hierarchy by
connection. Neither maps onto the other directly, but both map onto *samples* —
one transform per bone per frame — so that is what sits in the middle.

```
    hkx ──► HKX2 ──► spline fields ──► mopper ──► samples ──► FBX stack + curves
                                       (Havok)                        │
    hkx ◄── HKX2 ◄── spline fields ◄── mopper ◄── samples ◄───────────┘
```

Going through samples also means the Havok codec and the FBX layer never have to
know about each other.

## The three dependencies

| Package | For |
| --- | --- |
| `HKX2` | reading and writing the packfile, byte for byte |
| `LeanMeshIO` | the raw FBX node tree |
| `Mopper.Native` | Havok's own spline codec |

They come from GitHub Packages. See `nuget.config`: it names the feed and takes
credentials from `GITHUB_USERNAME` and `GITHUB_TOKEN`, so no secret is committed.

### Why the codec is a native process

Havok's spline compression is proprietary. Decoding it is documented well enough
to reimplement; encoding is curve fitting with quantization heuristics, and
reproducing Havok's choices well enough that a game behaves is a research
project with no oracle but the SDK. So the encoder is Havok's own, reached by
running `mopper.exe` as a child process — under Wine on Linux, which works
because it talks in files and exit codes.

It sits behind `IAnimationCodec`, so a managed decoder can be dropped in later
for read-only work without the rest of the converter noticing.

`Mopper.Native` copies `mopper.exe` beside the build output, so the default
probe finds it with no configuration.

## The skeleton is not optional

An animation names its bones only by track index, and which bone a track drives
comes from the binding. The names, the hierarchy and the rest pose live in a
separate skeleton `.hkx`. Both files are needed to produce an FBX anyone can
read.

## Testing

```sh
dotnet test                                          # everything that needs no game data
HKFBX_CORPUS=/path/to/extracted/meshes dotnet test   # and the rest
```

Most of the suite runs without a corpus: the interchange formats, the Euler
conversion, and the FBX writer, which is checked by building a document from a
synthetic skeleton and reading it back.

That structural check is the important one. FBX binds animation by connection,
so a file can carry every key and still play nothing — and it looks identical to
a working file until someone presses play. The tests assert the connections, not
just the numbers.

The corpus tests need real `.hkx` files and `mopper.exe`, and skip without them.

## Reading FBX back

`FbxAnimationReader` is the inverse, and it has to cope with files this project
did not write — an animator's export has been through a tool that knows nothing
about track indices or frame counts. So it assumes nothing about the keys:
curves are sampled onto a frame grid rather than read off one key per frame,
because an editor is free to drop keys it considers redundant, move them off
frame boundaries, or key each component at different times.

Two consequences worth knowing:

- **Bones come back in depth-first order from the roots**, not in file order.
  Havok requires a parent to precede its children and FBX promises nothing of
  the sort, so the order is rebuilt rather than trusted. Match bones up by name,
  not by index, when comparing against the skeleton you started with.
- **A bone with no curves holds its rest pose**, which is what an exporter that
  keys only the bones it moved leaves behind. Without that the skeleton
  collapses to the origin when the animation is applied.

## Status

The loop is closed: hkx to FBX to hkx, checked against real animations. A
chicken animation goes out as 33 tracks over 33 bones and comes back within
0.01 on both translation and rotation, and an hkx written from recompressed
curves reads back and samples the same.

Still open: nobody has opened one of these files in Blender. The structure is
asserted by the tests, but the axis convention and rotation order are
reasoned-about rather than confirmed, and that is worth doing before building
much on top.
