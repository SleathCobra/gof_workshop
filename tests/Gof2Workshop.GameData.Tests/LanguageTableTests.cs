using System.Security.Cryptography;
using System.Text;
using Gof2Workshop.Binary;
using Gof2Workshop.GameData;

namespace Gof2Workshop.GameData.Tests;

[TestClass]
public sealed class LanguageTableTests
{
    [TestMethod]
    public void ParserAndWriterRoundTripUtf8Exactly()
    {
        byte[] source = [
            0, 8, .. "Language"u8.ToArray(),
            0, 7, .. "Deutsch"u8.ToArray(),
            0, 7, .. "Grüße"u8.ToArray(),
            0, 0,
        ];

        LanguageTable table = new LanguageTableParser().Parse(new MemoryStream(source), "synthetic.lang");

        Assert.AreEqual(4, table.Entries.Count);
        Assert.AreEqual("Deutsch", table.LanguageName);
        Assert.AreEqual("Grüße", table.Entries[2].Value);
        CollectionAssert.AreEqual(source, new LanguageTableWriter().Write(table));
    }

    [TestMethod]
    public void ParserRejectsTruncatedAndInvalidUtf8()
    {
        FormatParseException truncated = Assert.Throws<FormatParseException>(
            () => new LanguageTableParser().Parse(new MemoryStream([0, 4, 1, 2]), "truncated.lang"));
        Assert.AreEqual(FormatFailureKind.Corrupt, truncated.FailureKind);
        Assert.AreEqual(4, truncated.Offset);

        FormatParseException invalid = Assert.Throws<FormatParseException>(
            () => new LanguageTableParser().Parse(new MemoryStream([0, 1, 0xFF]), "invalid.lang"));
        Assert.AreEqual("entry[0] UTF-8", invalid.Field);
    }

    [TestMethod]
    public void OperationsUndoRedoAndRecoveryAreDeterministic()
    {
        LanguageTable source = new([
            new LanguageEntry(0, "one", 0),
            new LanguageEntry(1, "two", 5),
        ]);
        LanguageEditSession session = new(source);
        session.Replace(1, "changed");
        Assert.AreEqual("changed", session.Working.Entries[1].Value);
        Assert.IsTrue(session.Undo());
        Assert.AreEqual("two", session.Working.Entries[1].Value);
        Assert.IsTrue(session.Redo());

        string recoveryJson = session.SerializeRecovery("abc123");
        LanguageRecoveryDocument recovery = System.Text.Json.JsonSerializer.Deserialize<LanguageRecoveryDocument>(
            recoveryJson)!;
        LanguageEditSession restored = new(source);
        restored.Replay(recovery, "ABC123");
        Assert.AreEqual("changed", restored.Working.Entries[1].Value);
        Assert.Throws<InvalidOperationException>(() => new LanguageEditSession(source).Replay(recovery, "other"));
        CollectionAssert.AreEqual(
            new LanguageTableWriter().Write(session.Working),
            new LanguageTableWriter().Write(restored.Working));
    }

    [TestMethod]
    public void LocalLanguageCorporaRoundTripWhenPresent()
    {
        string repository = FindRepositoryRoot();
        string[] corpora = ["data", "android_data", "ios_data", "macos_data", "ios_data2", "ios2_data"];
        int found = 0;
        foreach (string corpus in corpora)
        {
            string root = Path.Combine(repository, corpus);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(root, "*.lang", SearchOption.AllDirectories))
            {
                byte[] source = File.ReadAllBytes(path);
                LanguageTable table = new LanguageTableParser().Parse(new MemoryStream(source), path);
                byte[] reconstructed = new LanguageTableWriter().Write(table);
                Assert.AreEqual(
                    Convert.ToHexString(SHA256.HashData(source)),
                    Convert.ToHexString(SHA256.HashData(reconstructed)),
                    $"Language round trip differs for an entry in {corpus}.");
                found++;
            }
        }

        if (found == 0)
        {
            Assert.Inconclusive("No local .lang corpus files are present; synthetic tests still ran.");
        }
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
