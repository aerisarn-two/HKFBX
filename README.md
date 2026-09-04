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

## Format conventions

The first version of the writer produced files that imported as a correct
skeleton which would not move. Everything parsed, the hierarchy was right, every
curve was present, and nothing played. The conventions below are why, and each
is now pinned by a test in `FbxConventionTests`:

- **`Definitions` comes before `Objects`.** A reader sizes its tables from it
  before it reaches the objects it describes.
- **Objects carry the short class alias in their `Class::Name` prefix**, not the
  record's own name: `AnimStack::`, `AnimLayer::`, `AnimCurveNode::`,
  `AnimCurve::`. Readers key off the prefix.
- **`Documents` names the stack to play** through `ActiveAnimStackName`. Without
  it a file can hold a perfectly good take and open showing nothing moving.
- **`TimeMode` is 6**, `eFrames30`. The enum is not a frame rate — 11 is
  `eFrames24`.
- **Models carry `Version` 232**, and their `Lcl` properties are flagged `A+`,
  animatable *and animated*. `A` alone lets a reader ignore the curve.
- **A `Takes` list** is still consulted by some readers.

They were found by diffing against a file that works — a Mixamo export — rather
than by reading the specification, and the test suite compares against it
directly when it is to hand.

Three more lived a level down, in the binary container rather than the records,
and are fixed in LeanMeshIO 1.0.2: a missing `FileId`, a missing null record
after an empty node, and a missing null record after an object record with no
children. The first two get the file rejected outright. The third is worse — the
file loads, the skeleton is right, and the animation layer comes back with no
members, so every curve is present and drives nothing.

Output is verified against that SDK rather than against this project's own
reader: all 45 sample conversions load, and 44 of them animate. The one that
does not is a two-bone light whose motion is translation only.

## Bone names

Skyrim's bone names carry spaces, brackets and colons — `NPC L Finger02 [LF02]`
— and several 3D applications will not round trip them, so ck-cmd substitutes a
marker for each: `_s_`, `_ob_`, `_cb_`, `_dd_`.

Writing takes a `BoneNaming`: `Havok` keeps the names as they are, `CkCmd`
escapes them. Reading undoes the escaping unconditionally, so a file written
either way comes back with the names Havok expects, and a name carrying no
marker is unchanged.

The axis convention and rotation order are cross-checked against ck-cmd, which
does the same job in C++ through the FBX SDK: Z-up right-handed
(`FbxAxisSystem::Max`), centimetres, and Euler XYZ static in degrees
(`Eul_FromQuat(q, EulOrdXYZs)`).

## Root motion

An animation's root track and its root motion are different things. The track
animates the root bone in place; the motion is the travel across the ground,
which engines keep apart so it can drive a character controller rather than the
skeleton.

Where that motion is *stored* is not this library's concern. It may be in the
animation file, in a sidecar the engine ships, or computed — the library takes
it as keys and carries it through:

```csharp
var animation = sampled with
{
    RootMotion = new RootMotion
    {
        Duration = 1f,
        Translations = [new TranslationKey(1f, new Vector3(0f, 251.9f, 0f))],
        Rotations    = [new RotationKey(1f, Quaternion.Identity)],
    },
};

FbxAnimationWriter.Build(skeleton, animation, "walk").Save(path);
```

It is sampled onto the root bone, *replacing* its track rather than adding to
it — which is what ck-cmd does, and what a viewer needs if the character is to
travel. `FbxAnimationReader.ReadRootMotion(document, skeleton)` takes it back
off. Keys are sparse and need not land on frame boundaries.

Motion data commonly carries a single key holding the identity, which is a way
of saying there is no travel. `RootMotion.HasMovement` tells that apart from
motion that goes somewhere, and from carrying no keys at all.

### Where the tests get theirs

Skyrim keeps root motion in `animationdatasinglefile.txt` rather than the
`.hkx`, in a format that has nothing to do with Havok or with FBX. A reader for
it lives in the **test project**, under `tests/HKFBX.Tests/Skyrim/`, because
that is a source of real motion data to test against and not part of what this
library does. Anyone converting animations from elsewhere supplies their own
keys and never sees it.

It parses the file exactly — every one of its 170,724 lines, or it throws
naming the one that stopped it — because a motion block names only a cache
index, and the clip generators are what say which animation that index belongs
to. `project.MotionFor("TurnCannedL180")` does the join, the same one ck-cmd
makes in `findMovement`.

Across the 49 projects that ship a cache: 10,597 clips, 6,725 motion entries,
2,632 of them travelling and 511 turning.

A great many clips are named after the animation they play — `TurnCannedL180`
plays `turncannedl180.hkx` — so `MotionSamples.Find` pairs the two by name
within the project's own folder, and `CorpusMotionTests` puts the pairs through
the writer and reads them back off the disk. That is what tests the feature
against motion someone authored rather than motion invented in a fixture: clips
that turn a full 180, that sprint 2,000 units, that spin without travelling at
all.

Setting `HKFBX_SAMPLES` alongside `HKFBX_CORPUS` keeps the results and widens
the run to one clip from every project that has any:

```
HKFBX_CORPUS=~/Dev/BSAFileExtractor/extracted/meshes \
HKFBX_SAMPLES=~/Dev/hkfbx-samples/rootmotion \
  dotnet test --filter MotionAndEventsSurviveRealAnimations
```

That writes 42 files in about half a minute. Without it a spread of eight goes
to a temporary directory and is deleted, which keeps a normal run at a couple
of seconds. Either way it is the test doing it; there is no export tool.

## Events

An animation announces events as it plays — a footstep, a hit, the end of a clip
— which Havok stores as annotation tracks and a behaviour graph listens for.
They are named moments and carry no other meaning here:

```csharp
var animation = sampled with
{
    Annotations = [new AnnotationTrack
    {
        Name = "",
        Events = [new AnimationEvent(0.25f, "SoundPlay.NPCFootstep")],
    }],
};
```

`FbxAnimationReader.ReadEvents(document)` reads them back. In the FBX they ride
on the root bone as animated enum properties named `hkEvents…`, the
shape ck-cmd uses: the enum's values are the texts, and a curve says when each
fires, with constant keys because an event is a moment rather than something
that eases in.

One deliberate difference. ck-cmd splits a text such as
`SoundPlay.NPCChickenScratch` at its last capital and reassembles it on the way
back, which does not survive every name. The text is stored whole here, so it
round trips as written.

## Status

The loop is closed: hkx to FBX to hkx, checked against real animations. A
chicken animation goes out as 33 tracks over 33 bones and comes back within
0.01 on both translation and rotation, and an hkx written from recompressed
curves reads back and samples the same.

The reader also parses a file this project had no hand in writing, which is what
keeps it honest about being more than the writer's mirror.
