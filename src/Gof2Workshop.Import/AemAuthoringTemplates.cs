using System.Numerics;
using Gof2Workshop.Formats.Aem;

namespace Gof2Workshop.Import;

public enum AemAuthoringTemplate
{
    Empty,
    StaticProp,
    SingleMeshShip,
    MultiSubmeshShip,
    AnimatedObject,
    BillboardPlane,
    StationComponent,
}

/// <summary>
/// Independently authored, non-proprietary starting geometry. Templates use the same operation
/// path as imported geometry and are immediately writer/reparse-validatable.
/// </summary>
public static class AemAuthoringTemplateFactory
{
    public static void Populate(AemAuthoringProject project, AemAuthoringTemplate template)
    {
        ArgumentNullException.ThrowIfNull(project);
        switch (template)
        {
            case AemAuthoringTemplate.Empty:
                return;
            case AemAuthoringTemplate.StaticProp:
                project.AddPrimitive(Cube("Prop", Vector3.Zero, new Vector3(0.75f)));
                return;
            case AemAuthoringTemplate.SingleMeshShip:
                project.AddPrimitive(ShipBody("Hull"));
                return;
            case AemAuthoringTemplate.MultiSubmeshShip:
                project.AddPrimitive(ShipBody("Hull"));
                project.AddPrimitive(Cube("Port Wing", new Vector3(-1.15f, 0, 0), new Vector3(0.9f, 0.12f, 0.45f)));
                project.AddPrimitive(Cube("Starboard Wing", new Vector3(1.15f, 0, 0), new Vector3(0.9f, 0.12f, 0.45f)));
                return;
            case AemAuthoringTemplate.AnimatedObject:
                project.AddPrimitive(Cube("Animated Part", Vector3.Zero, new Vector3(0.5f)));
                string stableId = project.Current.Submeshes[0].StableId;
                project.ReplaceTrack(stableId, AemAnimationChannel.RotationY,
                [
                    new AemAuthoringKey(0, 0),
                    new AemAuthoringKey(1, MathF.PI),
                    new AemAuthoringKey(2, MathF.Tau),
                ]);
                return;
            case AemAuthoringTemplate.BillboardPlane:
                project.AddPrimitive(Plane("Billboard"));
                return;
            case AemAuthoringTemplate.StationComponent:
                project.AddPrimitive(Cube("Station Core", Vector3.Zero, new Vector3(0.8f, 0.8f, 0.8f)));
                project.AddPrimitive(Cube("Dock Arm", new Vector3(1.35f, 0, 0), new Vector3(0.65f, 0.18f, 0.18f)));
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(template));
        }
    }

    private static ImportedPrimitive ShipBody(string name)
    {
        Vector3[] positions =
        [
            new(0, 0.25f, -1.5f), new(-0.8f, -0.2f, 0.75f), new(0.8f, -0.2f, 0.75f),
            new(0, 0.55f, 0.55f), new(0, -0.45f, 0.4f),
        ];
        ushort[] indices = [0, 1, 3, 0, 3, 2, 0, 2, 4, 0, 4, 1, 1, 4, 3, 2, 3, 4];
        return WithGeneratedNormals(name, positions, indices);
    }

    private static ImportedPrimitive Plane(string name) => new(
        name,
        [new(-0.5f, -0.5f, 0), new(0.5f, -0.5f, 0), new(0.5f, 0.5f, 0), new(-0.5f, 0.5f, 0)],
        [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        [new(0, 1), new(1, 1), new(1, 0), new(0, 0)],
        null,
        [0, 1, 2, 0, 2, 3],
        null);

    private static ImportedPrimitive Cube(string name, Vector3 center, Vector3 half)
    {
        Vector3[] positions =
        [
            center + new Vector3(-half.X, -half.Y, -half.Z), center + new Vector3(half.X, -half.Y, -half.Z),
            center + new Vector3(half.X, half.Y, -half.Z), center + new Vector3(-half.X, half.Y, -half.Z),
            center + new Vector3(-half.X, -half.Y, half.Z), center + new Vector3(half.X, -half.Y, half.Z),
            center + new Vector3(half.X, half.Y, half.Z), center + new Vector3(-half.X, half.Y, half.Z),
        ];
        ushort[] indices =
        [
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2,
            0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5,
        ];
        return WithGeneratedNormals(name, positions, indices);
    }

    private static ImportedPrimitive WithGeneratedNormals(string name, Vector3[] positions, ushort[] indices) =>
        new(name, positions, null, null, null, indices, null);
}
