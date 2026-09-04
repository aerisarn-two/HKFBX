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
                                        (Havok)      │
    hkx ◄── HKX2 ◄── spline fields ◄── mopper ◄──────┘
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

## Status

Done: hkx to FBX, with the skeleton as a node hierarchy and the animation as one
stack of curves over it. The Havok round trip — decompress, recompress,
decompress — holds to about 0.002 on a real animation.

Next: FBX back to hkx, which closes the loop.
