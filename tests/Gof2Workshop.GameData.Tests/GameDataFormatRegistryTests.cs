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
