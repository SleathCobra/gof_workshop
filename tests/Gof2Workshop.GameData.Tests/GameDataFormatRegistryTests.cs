using System.Buffers.Binary;
using System.Security.Cryptography;
using Gof2Workshop.Binary;
using Gof2Workshop.GameData;

namespace Gof2Workshop.GameData.Tests;

[TestClass]
public sealed class GameDataFormatRegistryTests
{
    [TestMethod]
    public void SyntheticFamiliesParseEditUndoAndRoundTrip()
    {
        byte[] names = Build(writer =>
        {
            WriteInt(writer, 2);
            WriteString(writer, "Ayla");
            WriteString(writer, "Boro");
        });
        GameDataFormatRegistry registry = new();
        GameDataDocument document = registry.Parse("names_test_0.bin", names);
        Assert.AreEqual(GameDataFamily.Names, document.Family);
        Assert.AreEqual(2, document.Records.Count);
        CollectionAssert.AreEqual(names, new GameDataEditSession(document).Write());

        GameDataEditSession session = new(document);
        GameDataField field = document.Records[0].Fields.Single();
        session.Replace(field.Id, "Zyla");
        GameDataDocument changed = registry.Parse("names_test_0.bin", session.Write());
        Assert.AreEqual("Zyla", changed.Records[0].Fields.Single().Value);
        Assert.IsTrue(session.Undo());
        CollectionAssert.AreEqual(names, session.Write());
        Assert.IsTrue(session.Redo());

        byte[] ships = new byte[72];
        for (int index = 0; index < 18; index++)
        {
            BinaryPrimitives.WriteInt32BigEndian(ships.AsSpan(index * 4, 4), index - 4);
        }

        GameDataDocument shipDocument = registry.Parse("ships.bin", ships);
        Assert.AreEqual(2, shipDocument.Records.Count);
        GameDataEditSession shipSession = new(shipDocument);
        shipSession.Replace(shipDocument.Records[1].Fields[8].Id, "2048");
        Assert.AreEqual(2048, BinaryPrimitives.ReadInt32BigEndian(shipSession.Write().AsSpan(68, 4)));
    }

    [TestMethod]
    public void SizeChangingStructuralStringEditIsRejectedAndRecoveryChecksHash()
    {
        byte[] station = Build(writer =>
        {
            WriteString(writer, "Eden");
            for (int value = 0; value < 4; value++)
            {
                WriteInt(writer, value);
            }
        });
        GameDataDocument document = new GameDataFormatRegistry().Parse("stations.bin", station);
        GameDataEditSession session = new(document);
        GameDataField name = document.Records[0].Fields[0];
        Assert.Throws<InvalidOperationException>(() => session.Replace(name.Id, "Longer"));
        session.Replace(name.Id, "Ares");

        string recovery = session.SerializeRecovery("deadbeef");
        GameDataRecoveryDocument parsed = System.Text.Json.JsonSerializer.Deserialize<GameDataRecoveryDocument>(recovery)!;
        Assert.Throws<InvalidOperationException>(() => new GameDataEditSession(document).Replay(parsed, "changed"));
        GameDataEditSession replayed = new(document);
        replayed.Replay(parsed, "DEADBEEF");
        CollectionAssert.AreEqual(session.Write(), replayed.Write());
    }

    [TestMethod]
    public void TruncatedAndImpossibleCountsFailSafely()
    {
        GameDataFormatRegistry registry = new();
        Assert.Throws<FormatParseException>(() => registry.Parse("names_test.bin", [0, 0, 0, 1, 0, 5, 65]));
        FormatParseException count = Assert.Throws<FormatParseException>(
            () => registry.Parse("items.bin", [0x7F, 0xFF, 0xFF, 0xFF]));
        Assert.AreEqual(FormatFailureKind.Corrupt, count.FailureKind);
    }

    [TestMethod]
    public void CollisionGeometryIsTypedEditableAndSizeStable()
    {
        byte[] source = new byte[8 + (1 + 5 + 7) * 4];
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(0, 4), 17);
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(4, 4), 12);
        int[] payload = [2, 0, 10, 20, 30, 40, 1, -5, -6, -7, 8, 9, 10];
        for (int index = 0; index < payload.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(8 + index * 4, 4), payload[index]);
        }

        GameDataFormatRegistry registry = new();
        GameDataDocument document = registry.Parse("collision.bin", source);
        Assert.AreEqual(GameDataFamily.CollisionGeometry, document.Family);
        Assert.AreEqual(GameDataSupportLevel.StructuralReadWrite, document.SupportLevel);
        Assert.AreEqual("Radius", document.Records[0].Fields.Single(field => field.Name.EndsWith("Radius", StringComparison.Ordinal)).Name.Split('.').Last());

        GameDataEditSession session = new(document);
        GameDataField radius = document.Records[0].Fields.Single(field => field.Name.EndsWith("Radius", StringComparison.Ordinal));
        session.Replace(radius.Id, "55");
        byte[] changed = session.Write();
        Assert.AreEqual(55, BinaryPrimitives.ReadInt32LittleEndian(changed.AsSpan(radius.Offset, 4)));
        Assert.AreEqual(2, registry.Parse("collision.bin", changed).Records[0].Fields.Count(field => field.Name.EndsWith("Type", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(source, new GameDataEditSession(document).Write());
    }

    [TestMethod]
    public void DockAndWeaponPositionFloatsUseObservedLittleEndianLayout()
    {
        byte[] dock = new byte[42];
        BinaryPrimitives.WriteUInt16LittleEndian(dock.AsSpan(0, 2), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(dock.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dock.AsSpan(4, 2), 2);
        for (int index = 0; index < 9; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(dock.AsSpan(6 + index * 4, 4), index + 0.25f);
        }

        GameDataFormatRegistry registry = new();
        GameDataDocument docking = registry.Parse("docks.bin", dock);
        Assert.AreEqual("0.25", docking.Records[0].Fields[3].Value);
        Assert.AreEqual(GameDataScalarKind.Float32LittleEndian, docking.Records[0].Fields[3].Kind);

        byte[] weapon = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(weapon.AsSpan(0, 2), 44);
        BinaryPrimitives.WriteUInt16LittleEndian(weapon.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(weapon.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteInt16LittleEndian(weapon.AsSpan(6, 2), -472);
        BinaryPrimitives.WriteSingleLittleEndian(weapon.AsSpan(12, 4), 0.4f);
        BinaryPrimitives.WriteSingleLittleEndian(weapon.AsSpan(16, 4), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(weapon.AsSpan(20, 4), 0.6f);
        GameDataDocument positions = registry.Parse("weapons_hd.bin", weapon);
        Assert.AreEqual(GameDataFamily.WeaponPositions, positions.Family);
        Assert.AreEqual("-472", positions.Records[0].Fields[3].Value);
        Assert.AreEqual("0.4", positions.Records[0].Fields[6].Value);
        CollectionAssert.AreEqual(weapon, new GameDataEditSession(positions).Write());
    }

    [TestMethod]
    public void UnknownCollisionShapeAndTruncatedDockFailAtControlledFields()
    {
        byte[] collision = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(collision.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(collision.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(collision.AsSpan(12, 4), 9);
        FormatParseException shape = Assert.Throws<FormatParseException>(
            () => new GameDataFormatRegistry().Parse("collision.bin", collision));
        Assert.AreEqual("collision shape type", shape.Field);

        FormatParseException dock = Assert.Throws<FormatParseException>(
            () => new GameDataFormatRegistry().Parse("docks.bin", [1, 0, 1, 0, 2, 0]));
        StringAssert.Contains(dock.Message, "space point count");
    }

    [TestMethod]
    public void EveryLocalBinIsClassifiedParsedAndByteIdenticalWhenPresent()
    {
        string root = FindRepositoryRoot();
        string[] corpora = ["data", "android_data", "ios_data", "macos_data", "ios_data2", "ios2_data"];
        GameDataFormatRegistry registry = new();
        List<string> failures = [];
        int total = 0;
        foreach (string corpus in corpora)
        {
            string directory = Path.Combine(root, corpus);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(directory, "*.bin", SearchOption.AllDirectories))
            {
                total++;
                byte[] source = File.ReadAllBytes(path);
                try
                {
                    GameDataDocument document = registry.Parse(Path.GetFileName(path), source);
                    if (document.Family == GameDataFamily.Unknown)
                    {
                        failures.Add($"{corpus}/{Path.GetFileName(path)}: unclassified");
                        continue;
                    }

                    byte[] output = new GameDataEditSession(document).Write();
                    if (!SHA256.HashData(source).SequenceEqual(SHA256.HashData(output)))
                    {
                        failures.Add($"{corpus}/{Path.GetFileName(path)}: unchanged round trip differs");
                    }
                }
                catch (Exception exception)
                {
                    failures.Add($"{corpus}/{Path.GetFileName(path)}: {exception.Message}");
                }
            }
        }

        if (total == 0)
        {
            Assert.Inconclusive("No local BIN corpora are present.");
        }

        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures.Take(20)));
    }

    private static byte[] Build(Action<MemoryStream> build)
    {
        using MemoryStream stream = new();
        build(stream);
        return stream.ToArray();
    }

    private static void WriteInt(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteString(Stream output, string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        output.Write(length);
        output.Write(bytes);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalaxyOnFire2Workshop.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
