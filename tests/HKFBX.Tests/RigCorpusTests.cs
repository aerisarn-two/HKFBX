using System.Numerics;
using HKFBX.Fbx;
using HKFBX.Hkx;
using HKFBX.Model;
using LeanMeshIO;

namespace HKFBX.Tests;

/// <summary>
/// Every skeleton the game ships, out to FBX, back, and into a packfile.
/// </summary>
/// <remarks>
/// The chain end to end: hkx to model, model to FBX, FBX to model, model back
/// into a packfile through the original as a template, and the packfile read
/// again. What comes out is compared with what went in.
///
/// Nothing is written to the corpus: the FBX and the packfile go to a temporary
/// folder, and the corpus file is only ever read.
/// </remarks>
public class RigCorpusTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("hkfbx-rig-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* a temp folder */ }
    }

    private static IEnumerable<string> Skeletons() =>
        Directory.EnumerateFiles(Corpus.Root!, "skeleton*.hkx", SearchOption.AllDirectories).Order();

    private static IEnumerable<SkeletonFile> Ragdolls()
    {
        foreach (string path in Skeletons())
        {
            SkeletonFile file;

            try { file = HkxSkeletonFile.Read(path); }
            catch { continue; }

            if (file.HasRagdoll) yield return file;
        }
    }

    /// <summary>
    /// The design rests on this: a body per ragdoll bone, of the same name, so the
    /// ragdoll skeleton need not be written into the FBX at all.
    /// </summary>
    [CorpusFact]
    public void EveryRagdollBoneHasABodyOfItsOwnName()
    {
        int checkedFiles = 0;

        foreach (SkeletonFile file in Ragdolls())
        {
            var bodies = file.Bodies.Where(body => body.InRagdoll).Select(body => body.Name).ToList();

            Assert.Equal(file.Ragdoll!.Bones.Select(bone => bone.Name), bodies);
            checkedFiles++;
        }

        Assert.True(checkedFiles >= 40, $"only {checkedFiles} skeletons carried a ragdoll");
    }

    /// <summary>
    /// And this: the joints are the ragdoll's hierarchy, one per parent link.
    /// </summary>
    [CorpusFact]
    public void TheJointsAreTheRagdollsHierarchy()
    {
        foreach (SkeletonFile file in Ragdolls())
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ragdoll = file.Ragdoll!;

            for (int i = 0; i < ragdoll.Count; i++) index[ragdoll.Bones[i].Name] = i;

            int joined = file.Joints.Count(joint =>
                index.TryGetValue(joint.BodyA, out int child)
                && index.TryGetValue(joint.BodyB, out int parent)
                && ragdoll.Bones[child].ParentIndex == parent);

            Assert.Equal(file.Joints.Count, joined);
        }
    }

    /// <summary>
    /// Which rig bone a body drives is carried rather than derived, because the
    /// naming does not survive contact with the creatures: the human prefixes, the
    /// bear renumbers, the chicken shifts by one.
    /// </summary>
    [CorpusFact]
    public void TheRigMappingIsNotGuessableFromTheNames()
    {
        int guessable = 0, total = 0;

        foreach (SkeletonFile file in Ragdolls())
            foreach (RagdollBody body in file.Bodies.Where(b => b.RigBone is not null))
            {
                total++;

                if (string.Equals(body.Name, "Ragdoll_" + body.RigBone, StringComparison.OrdinalIgnoreCase))
                    guessable++;
            }

        Assert.True(total > 400, $"only {total} bodies mapped to a rig bone");
        Assert.True(guessable < total * 0.6, $"{guessable} of {total} would have been guessable, so the carrying may be unnecessary");
    }

    /// <summary>
    /// The whole chain, over every ragdoll in the game.
    /// </summary>
    /// <remarks>
    /// Positions, shapes and limits come back exactly -- they are doubles in the
    /// FBX and floats either side of it. Rotations do not, and cannot: FBX stores
    /// an orientation as three Euler angles, and near the poles that decomposition
    /// is ill-conditioned whichever way it is taken. A twentieth of a degree is
    /// the format's floor.
    /// </remarks>
    [CorpusFact]
    public void EveryRagdollSurvivesTheWholeChain()
    {
        int done = 0;
        double worstRotation = 0;

        foreach (string path in Skeletons())
        {
            SkeletonFile before;

            try { before = HkxSkeletonFile.Read(path); }
            catch { continue; }

            if (!before.HasRagdoll) continue;

            FbxDocument document = FbxSkeletonWriter.Build(before);
            string fbx = Path.Combine(_temp, "rig.fbx");

            using (FileStream stream = File.Create(fbx)) document.Save(stream);

            FbxDocument reloaded;
            using (FileStream stream = File.OpenRead(fbx)) reloaded = FbxDocument.Load(stream);

            SkeletonFile middle = FbxSkeletonReader.Read(reloaded, before.FloatSlots);

            string rewritten = Path.Combine(_temp, "skeleton.hkx");
            HkxSkeletonFile.Write(path, middle, rewritten);

            SkeletonFile after = HkxSkeletonFile.Read(rewritten);
            done++;

            Assert.Equal(before.Rig.Count, after.Rig.Count);
            Assert.Equal(before.Bodies.Count, after.Bodies.Count);
            Assert.Equal(before.Joints.Count, after.Joints.Count);

            for (int i = 0; i < before.Rig.Count; i++)
            {
                Assert.True(
                    Vector3.Distance(before.Rig.Bones[i].ReferencePose.Translation,
                                     after.Rig.Bones[i].ReferencePose.Translation) < 1e-4f,
                    $"{Path.GetFileName(path)}: bone {before.Rig.Bones[i].Name} moved");

                worstRotation = Math.Max(worstRotation,
                    Drift(before.Rig.Bones[i].ReferencePose.Rotation, after.Rig.Bones[i].ReferencePose.Rotation));
            }

            foreach (RagdollBody body in before.Bodies)
            {
                RagdollBody copy = after.Bodies.Single(other => other.Name == body.Name);

                Assert.True(Vector3.Distance(body.Transform.Translation, copy.Transform.Translation) < 1e-4f);
                Assert.Equal(body.CollisionFilterInfo, copy.CollisionFilterInfo);
                Assert.Equal(body.Friction, copy.Friction, 4);

                if (body.Shape is { } shape)
                {
                    Assert.True(Vector3.Distance(shape.VertexA, copy.Shape!.VertexA) < 1e-4f);
                    Assert.Equal(shape.Radius, copy.Shape.Radius, 4);
                }
            }

            foreach (RagdollJoint joint in before.Joints)
            {
                RagdollJoint copy = after.Joints.Single(
                    other => other.BodyA == joint.BodyA && other.BodyB == joint.BodyB);

                Assert.Equal(joint.Kind, copy.Kind);
                Assert.Equal(joint.ConeMaxAngle, copy.ConeMaxAngle, 5);
                Assert.Equal(joint.TwistMinAngle, copy.TwistMinAngle, 5);
                Assert.Equal(joint.PlaneMaxAngle, copy.PlaneMaxAngle, 5);
                Assert.Equal(joint.HingeMinAngle, copy.HingeMinAngle, 5);

                Assert.True(Vector3.Distance(joint.FrameA.Translation, copy.FrameA.Translation) < 1e-3f);
                Assert.True(Vector3.Distance(joint.FrameB.Translation, copy.FrameB.Translation) < 1e-3f);
            }
        }

        Assert.True(done >= 40, $"only {done} ragdolls were checked");
        Assert.True(worstRotation < 0.05, $"worst rotation drift {worstRotation:F4} degrees");
    }

    /// <summary>The angle between two rotations, measured where acos is not usable.</summary>
    private static double Drift(Quaternion a, Quaternion b)
    {
        a = Quaternion.Normalize(a);
        b = Quaternion.Normalize(b);

        if (Quaternion.Dot(a, b) < 0) b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);

        double chord = Math.Sqrt(
            Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) +
            Math.Pow(a.Z - b.Z, 2) + Math.Pow(a.W - b.W, 2));

        return 2.0 * Math.Asin(Math.Clamp(chord / 2.0, 0, 1)) * 180.0 / Math.PI;
    }
}
