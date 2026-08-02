using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Gof2Workshop.GameData;

public sealed record GameDataCorpusSource(string ProfileId, string Root);

public sealed record GameDataFileValidation(
    string ProfileId,
    string FileName,
    GameDataFamily Family,
    GameDataSupportLevel SupportLevel,
    int ByteLength,
    int RecordCount,
    int EditableFieldCount,
    bool Parsed,
    bool ByteIdenticalRoundTrip,
    bool EditedRoundTrip,
    string? EditField,
    string? Failure);

public sealed record GameDataFamilySupportRow(
    GameDataFamily Family,
    IReadOnlyList<string> KnownFileNames,
    IReadOnlyList<string> Platforms,
    int FileCount,
    string Signature,
    string HeaderLayout,
    string RecordLayout,
    IReadOnlyList<string> KnownFields,
    IReadOnlyList<string> UnknownFields,
    IReadOnlyList<string> References,
    GameDataSupportLevel ReadWriteStatus,
    int ByteIdenticalRoundTrips,
    int EditedRoundTrips,
    int EditedRoundTripAttempts,
    string EditorType,
    bool RecordCreationSupported,
    string RealGameValidationStatus,
    string Confidence,
    IReadOnlyList<string> Blockers);

public sealed record GameDataSupportMatrixReport(
    int FormatVersion,
    DateTimeOffset CreatedAt,
    TimeSpan Elapsed,
    int TotalFiles,
    int ParsedFiles,
    int ByteIdenticalRoundTrips,
    int EditedRoundTrips,
    IReadOnlyList<GameDataFamilySupportRow> Families,
    IReadOnlyList<GameDataFileValidation> Files)
{
    public string ToMarkdown()
    {
        StringBuilder text = new();
        text.AppendLine("# Generated GOF2 BIN support matrix");
        text.AppendLine();
        text.AppendLine("> Generated from local ignored corpora. It contains filenames and aggregate structure facts, never proprietary bytes or absolute paths.");
        text.AppendLine();
        text.AppendFormat(CultureInfo.InvariantCulture, "Generated: {0:O}  ", CreatedAt).AppendLine();
        text.AppendFormat(CultureInfo.InvariantCulture,
            "Files: {0:N0}; parsed: {1:N0}; unchanged byte-identical: {2:N0}; controlled edited round trips: {3:N0}.  ",
            TotalFiles, ParsedFiles, ByteIdenticalRoundTrips, EditedRoundTrips).AppendLine();
        text.AppendFormat(CultureInfo.InvariantCulture, "Elapsed: {0:N0} ms.", Elapsed.TotalMilliseconds).AppendLine();
        text.AppendLine();
        text.AppendLine("| Family | Platforms | Files | Support | Unchanged | Edited | Editor | Creation | Confidence | Blockers |");
        text.AppendLine("|---|---|---:|---|---:|---:|---|---|---|---|");
        foreach (GameDataFamilySupportRow row in Families)
        {
            text.Append("| ").Append(Escape(row.Family.ToString())).Append(" | ")
                .Append(Escape(string.Join(", ", row.Platforms))).Append(" | ")
                .Append(row.FileCount.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Escape(row.ReadWriteStatus.ToString())).Append(" | ")
                .Append(row.ByteIdenticalRoundTrips.ToString(CultureInfo.InvariantCulture)).Append('/').Append(row.FileCount.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(row.EditedRoundTrips.ToString(CultureInfo.InvariantCulture)).Append('/').Append(row.EditedRoundTripAttempts.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Escape(row.EditorType)).Append(" | ")
                .Append(row.RecordCreationSupported ? "Experimental" : "Disabled").Append(" | ")
                .Append(Escape(row.Confidence)).Append(" | ")
                .Append(Escape(string.Join("; ", row.Blockers))).AppendLine(" |");
        }

        text.AppendLine();
        foreach (GameDataFamilySupportRow row in Families)
        {
            text.Append("## ").AppendLine(row.Family.ToString());
            text.AppendLine();
            text.Append("Files: ").AppendLine(string.Join(", ", row.KnownFileNames.Select(value => $"`{value}`")));
            text.Append("Signature/detection: ").AppendLine(row.Signature);
            text.Append("Header: ").AppendLine(row.HeaderLayout);
            text.Append("Records: ").AppendLine(row.RecordLayout);
            text.Append("Known fields: ").AppendLine(row.KnownFields.Count == 0 ? "None semantically named." : string.Join(", ", row.KnownFields.Select(value => $"`{value}`")));
            text.Append("Unknown fields: ").AppendLine(row.UnknownFields.Count == 0 ? "None currently exposed." : string.Join(", ", row.UnknownFields.Select(value => $"`{value}`")));
            text.Append("References: ").AppendLine(row.References.Count == 0 ? "No confirmed foreign-key semantics." : string.Join("; ", row.References));
            text.Append("Real-game status: ").AppendLine(row.RealGameValidationStatus);
            text.AppendLine();
        }

        return text.ToString();
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}

/// <summary>
/// Performs bounded parse, unchanged reconstruction and one size-stable controlled edit per file.
/// It is suitable for ignored local corpora and never retains file contents in its report.
/// </summary>
public sealed class GameDataSupportMatrixBuilder
{
    public GameDataSupportMatrixReport Build(
        IEnumerable<GameDataCorpusSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Stopwatch timer = Stopwatch.StartNew();
        GameDataFormatRegistry registry = new();
        List<GameDataFileValidation> files = [];
        Dictionary<(GameDataFamily Family, string Profile, string Name), GameDataDocument> documents = [];
        foreach (GameDataCorpusSource source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(source.Root))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(source.Root, "*.bin", SearchOption.AllDirectories)
                         .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path);
                byte[] bytes = File.ReadAllBytes(path);
                GameDataFormatDescriptor descriptor = registry.Detect(name);
                try
                {
                    GameDataDocument document = registry.Parse(name, bytes);
                    GameDataEditSession unchanged = new(document);
                    bool identical = unchanged.Write().AsSpan().SequenceEqual(bytes);
                    bool edited = false;
                    string? editField = null;
                    GameDataField? candidate = document.Records.SelectMany(record => record.Fields)
                        .FirstOrDefault(field => field.Editable && TryAlternative(field, out _));
                    int attempts = candidate is null ? 0 : 1;
                    if (candidate is not null && TryAlternative(candidate, out string? replacement))
                    {
                        GameDataEditSession edit = new(document);
                        edit.Replace(candidate.Id, replacement);
                        byte[] changed = edit.Write();
                        GameDataDocument reparsed = registry.Parse(name, changed);
                        GameDataField? reparsedField = reparsed.Records.SelectMany(record => record.Fields)
                            .FirstOrDefault(field => field.Id == candidate.Id);
                        edited = !changed.AsSpan().SequenceEqual(bytes) && reparsedField?.Value == replacement;
                        editField = candidate.Name;
                    }

                    documents[(document.Family, source.ProfileId, name)] = document;
                    files.Add(new GameDataFileValidation(
                        source.ProfileId, name, document.Family, document.SupportLevel, bytes.Length,
                        document.Records.Count, document.EditableFieldCount, true, identical, edited,
                        attempts == 0 ? null : editField, null));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or OverflowException or ArgumentException)
                {
                    files.Add(new GameDataFileValidation(
                        source.ProfileId, name, descriptor.Family, descriptor.SupportLevel, bytes.Length,
                        0, 0, false, false, false, null, exception.Message));
                }
            }
        }

        IReadOnlyList<GameDataFamilySupportRow> families = files
            .GroupBy(file => file.Family)
            .OrderBy(group => group.Key)
            .Select(group => BuildFamily(group.Key, group.ToArray(), documents
                .Where(pair => pair.Key.Family == group.Key)
                .Select(pair => pair.Value)
                .ToArray()))
            .ToArray();
        timer.Stop();
        return new GameDataSupportMatrixReport(
            1,
            DateTimeOffset.UtcNow,
            timer.Elapsed,
            files.Count,
            files.Count(file => file.Parsed),
            files.Count(file => file.ByteIdenticalRoundTrip),
            files.Count(file => file.EditedRoundTrip),
            families,
            new ReadOnlyCollection<GameDataFileValidation>(files));
    }

    private static GameDataFamilySupportRow BuildFamily(
        GameDataFamily family,
        GameDataFileValidation[] files,
        IReadOnlyList<GameDataDocument> documents)
    {
        (string signature, string header, string records, string[] references, string[] blockers) = Describe(family);
        string[] known = documents.SelectMany(document => document.Records).SelectMany(record => record.Fields)
            .Where(field => !field.Confidence.Contains("unresolved", StringComparison.OrdinalIgnoreCase) &&
                            !field.Name.Contains("Count", StringComparison.Ordinal))
            .Select(field => Generalize(field.Name)).Distinct(StringComparer.Ordinal).Order().ToArray();
        string[] unknown = documents.SelectMany(document => document.Records).SelectMany(record => record.Fields)
            .Where(field => field.Confidence.Contains("unresolved", StringComparison.OrdinalIgnoreCase))
            .Select(field => Generalize(field.Name)).Distinct(StringComparer.Ordinal).Order().ToArray();
        GameDataSupportLevel status = files.Select(file => file.SupportLevel).DefaultIfEmpty(GameDataSupportLevel.UnknownAndIsolated).Max();
        return new GameDataFamilySupportRow(
            family,
            files.Select(file => file.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(),
            files.Select(file => file.ProfileId).Distinct(StringComparer.Ordinal).Order().ToArray(),
            files.Length,
            signature,
            header,
            records,
            known,
            unknown,
            references,
            status,
            files.Count(file => file.ByteIdenticalRoundTrip),
            files.Count(file => file.EditedRoundTrip),
            files.Count(file => file.EditField is not null),
            status == GameDataSupportLevel.SemanticReadWrite ? "Semantic grid/form + raw research view" : "Typed structural grid/form + raw research view",
            RecordCreationSupported: false,
            "Writer/reparse validated locally; in-game mutation has not been performed by this command.",
            status == GameDataSupportLevel.SemanticReadWrite ? "Confirmed engine consumer and repeated corpus layout" : "Confirmed record layout; unresolved fields remain explicitly labeled",
            blockers);
    }

    private static (string Signature, string Header, string Records, string[] References, string[] Blockers) Describe(GameDataFamily family) => family switch
    {
        GameDataFamily.Names => ("names_*.bin filename consumer", "BE int32 count", "Java modified UTF-8 strings", [], ["Record creation is disabled pending name-pool capacity validation."]),
        GameDataFamily.ItemsAndBlueprints => ("items.bin filename consumer", "No global header", "Three BE int32 counted arrays per record", ["Blueprint component IDs refer to item records."], ["Individual attribute indices remain unresolved."]),
        GameDataFamily.Ships => ("ships.bin filename consumer", "No global header", "Nine BE int32 values per record", ["Ship IDs are referenced by wanted and other runtime records."], ["New ship capacity and model registration are executable-dependent."]),
        GameDataFamily.SystemsAndConnections => ("systems.bin filename consumer", "No global header", "Modified UTF name, eight BE int32 fields, four counted arrays", ["Station IDs and neighbour system IDs are encoded."], ["LegacyOrStaticIds semantics remain unresolved."]),
        GameDataFamily.Stations => ("stations.bin filename consumer", "No global header", "Modified UTF name plus four BE int32 fields", ["SystemId is an encoded system reference."], ["New-station capacity is not in-game validated."]),
        GameDataFamily.Agents => ("agents.bin filename consumer", "No global header", "Variant-sensitive modified UTF and BE scalar records", ["Station, system and blueprint IDs are encoded."], ["MobileVariantParameter semantics remain unresolved."]),
        GameDataFamily.WantedTargets => ("wanted.bin filename consumer", "No global header", "Modified UTF, thirteen BE int32 fields, optional five face bytes", ["Ship, item and native campaign-step references are encoded."], ["Encounter spawning remains native runtime behavior."]),
        GameDataFamily.NewsTicker => ("ticker.bin filename consumer", "No global header", "Seven BE int32 fields per record", [], ["Condition flag meanings are only structurally enumerated."]),
        GameDataFamily.ShipParts => ("shipparts.bin filename consumer", "No global header", "Group/count byte prefix and fixed mixed-width transforms", ["Resource IDs refer to model resources."], ["Resource-ID-to-path map is not encoded in this table."]),
        GameDataFamily.StationParts => ("stationparts.bin filename consumer", "No global header", "Group/hangar/count prefix and fixed mixed-width transforms", ["Hangar/resource IDs refer to model resources."], ["Resource-ID-to-path map is not encoded in this table."]),
        GameDataFamily.CollisionGeometry => ("collision*.bin filename consumers", "LE owner and payload-word count-minus-one", "LE shape count followed by sphere or AABB records", ["Owner IDs correlate with model/resource consumers."], ["Source coordinate scale/axis semantics remain profile-dependent."]),
        GameDataFamily.DockingPoints => ("docks*.bin and *_docking_points*.bin consumers", "LE int16 owner and count", "38-byte typed position/rotation/auxiliary records", ["Owner IDs correlate with station/model resources."], ["Final auxiliary float3 gameplay meaning remains unresolved."]),
        GameDataFamily.WeaponPositions => ("weapons*.bin and *_weapons.bin consumers", "LE int16 owner and count", "LE int16 type/position with optional direction float3", ["Owner IDs correlate with ship/model resources."], ["Complete point-type enum is not confirmed."]),
        _ => ("Isolated unknown", "Unknown", "Opaque", [], ["No safe semantic or structural representation is confirmed."]),
    };

    private static string Generalize(string name)
    {
        int bracket = name.IndexOf('[', StringComparison.Ordinal);
        if (bracket < 0)
        {
            return name;
        }

        int close = name.IndexOf(']', bracket);
        return close < 0 ? name[..bracket] + "[]" : name[..bracket] + "[]" + name[(close + 1)..];
    }

    private static bool TryAlternative(GameDataField field, out string replacement)
    {
        replacement = field.Value;
        switch (field.Kind)
        {
            case GameDataScalarKind.Int32BigEndian:
            case GameDataScalarKind.Int32LittleEndian:
                int value32 = int.Parse(field.Value, CultureInfo.InvariantCulture);
                replacement = (value32 == int.MaxValue ? value32 - 1 : value32 + 1).ToString(CultureInfo.InvariantCulture);
                return true;
            case GameDataScalarKind.Int16BigEndian:
            case GameDataScalarKind.Int16LittleEndian:
                short value16 = short.Parse(field.Value, CultureInfo.InvariantCulture);
                replacement = (value16 == short.MaxValue ? value16 - 1 : value16 + 1).ToString(CultureInfo.InvariantCulture);
                return true;
            case GameDataScalarKind.Float32LittleEndian:
                float value = float.Parse(field.Value, CultureInfo.InvariantCulture);
                float changed = value == 0 ? 0.25f : value * 0.5f;
                if (!float.IsFinite(changed) || changed == value)
                {
                    changed = 0;
                }
                replacement = changed.ToString("R", CultureInfo.InvariantCulture);
                return changed != value;
            case GameDataScalarKind.UnsignedByte:
                byte byteValue = byte.Parse(field.Value, CultureInfo.InvariantCulture);
                replacement = (byteValue == byte.MaxValue ? byteValue - 1 : byteValue + 1).ToString(CultureInfo.InvariantCulture);
                return true;
            case GameDataScalarKind.ModifiedUtf8:
                int encodedLength = field.Length - 2;
                if (encodedLength <= 0)
                {
                    return false;
                }
                replacement = new string(field.Value == new string('X', encodedLength) ? 'Y' : 'X', encodedLength);
                return true;
            case GameDataScalarKind.RawBytes:
                if (field.Value.Length < 2)
                {
                    return false;
                }
                replacement = (field.Value.StartsWith("00", StringComparison.Ordinal) ? "01" : "00") + field.Value[2..];
                return true;
            default:
                return false;
        }
    }
}
