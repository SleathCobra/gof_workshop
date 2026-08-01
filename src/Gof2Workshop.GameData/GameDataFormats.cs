using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gof2Workshop.Binary;

namespace Gof2Workshop.GameData;

public enum GameDataFamily
{
    Names,
    ItemsAndBlueprints,
    Ships,
    SystemsAndConnections,
    Stations,
    Agents,
    WantedTargets,
    NewsTicker,
    ShipParts,
    StationParts,
    CollisionGeometry,
    DockingPoints,
    WeaponPositions,
    WeaponsAndEquipment,
    Unknown,
}

public enum GameDataSupportLevel
{
    SemanticReadWrite,
    StructuralReadWrite,
    LossPreservingAdvancedEditor,
    RecognizedReadOnly,
    UnknownAndIsolated,
}

public enum GameDataScalarKind
{
    Int32BigEndian,
    Int16BigEndian,
    Int16LittleEndian,
    UnsignedByte,
    ModifiedUtf8,
    RawBytes,
}

public sealed record GameDataField(
    string Id,
    string Name,
    int Offset,
    int Length,
    GameDataScalarKind Kind,
    string Value,
    bool Editable,
    string Confidence);

public sealed record GameDataRecord(
    int Index,
    int Offset,
    int Length,
    IReadOnlyList<GameDataField> Fields);

public sealed record GameDataDocument(
    string Name,
    GameDataFamily Family,
    GameDataSupportLevel SupportLevel,
    string Endianness,
    byte[] OriginalBytes,
    IReadOnlyList<GameDataRecord> Records,
    IReadOnlyList<string> Warnings)
{
    public int EditableFieldCount => Records.SelectMany(record => record.Fields).Count(scalar => scalar.Editable);
}

public sealed record GameDataFormatDescriptor(
    GameDataFamily Family,
    GameDataSupportLevel SupportLevel,
    string DisplayName,
    string Endianness,
    string DetectionReason);

/// <summary>
/// Detects the separately structured GOF binary-table families. Detection is
/// intentionally filename based because the observed files do not share a
/// magic header and the engine selects each consumer by resource name.
/// </summary>
public sealed class GameDataFormatRegistry
{
    public GameDataFormatDescriptor Detect(string name)
    {
        string file = Path.GetFileName(name).ToLowerInvariant();
        if (file.StartsWith("names_", StringComparison.Ordinal) && file.EndsWith(".bin", StringComparison.Ordinal))
        {
            return Semantic(GameDataFamily.Names, "Character name table", "big-endian / Java modified UTF-8");
        }

        return file switch
        {
            "items.bin" => Structural(GameDataFamily.ItemsAndBlueprints, "Items and blueprint arrays", "big-endian"),
            "ships.bin" => Structural(GameDataFamily.Ships, "Ship parameter table", "big-endian"),
            "systems.bin" => Structural(GameDataFamily.SystemsAndConnections, "Systems and connection table", "big-endian / Java modified UTF-8"),
            "stations.bin" => Semantic(GameDataFamily.Stations, "Station table", "big-endian / Java modified UTF-8"),
            "agents.bin" => Structural(GameDataFamily.Agents, "Agent table", "big-endian / Java modified UTF-8"),
            "wanted.bin" => Structural(GameDataFamily.WantedTargets, "Wanted-target table", "big-endian / Java modified UTF-8"),
            "ticker.bin" => Structural(GameDataFamily.NewsTicker, "News/ticker table", "big-endian"),
            "shipparts.bin" => Structural(GameDataFamily.ShipParts, "Ship attachment transforms", "mixed: byte records and big-endian values"),
            "stationparts.bin" => Structural(GameDataFamily.StationParts, "Station attachment transforms", "mixed: byte records and big-endian values"),
            "collision.bin" or "collision_test.bin" or "wreck_collisions.bin" or "static_collisions.bin" or "v_collisions.bin" =>
                Advanced(GameDataFamily.CollisionGeometry, "Collision records", "little-endian record envelope"),
            "docks.bin" or "docks_hd.bin" => Advanced(GameDataFamily.DockingPoints, "Docking-point records", "platform-specific little-endian records"),
            "weapons.bin" or "weapons_hd.bin" or "weapons_sd.bin" =>
                Advanced(GameDataFamily.WeaponsAndEquipment, "Weapon parameter table", "platform-specific structured table"),
            _ when file.EndsWith("_weapons.bin", StringComparison.Ordinal) =>
                Structural(GameDataFamily.WeaponPositions, "Weapon attachment positions", "mixed: little-endian envelope and big-endian payload"),
            _ when file.Contains("docking_points", StringComparison.Ordinal) =>
                Advanced(GameDataFamily.DockingPoints, "Docking-point records", "platform-specific little-endian records"),
            _ => new GameDataFormatDescriptor(
                GameDataFamily.Unknown,
                GameDataSupportLevel.UnknownAndIsolated,
                "Unknown BIN family",
                "unknown",
                "No confirmed filename consumer or signature matched."),
        };
    }

    public GameDataDocument Parse(string name, ReadOnlySpan<byte> bytes)
    {
        byte[] source = bytes.ToArray();
        GameDataFormatDescriptor descriptor = Detect(name);
        try
        {
            return descriptor.Family switch
            {
                GameDataFamily.Names => ParseNames(name, descriptor, source),
                GameDataFamily.ItemsAndBlueprints => ParseItems(name, descriptor, source),
                GameDataFamily.Ships => ParseFixedIntRecords(name, descriptor, source, 9,
                    ["Parameter0", "Parameter1", "Parameter2", "Parameter3", "Parameter4", "Parameter5", "Parameter6", "Parameter7", "Speed"]),
                GameDataFamily.SystemsAndConnections => ParseSystems(name, descriptor, source),
                GameDataFamily.Stations => ParseStationRecords(name, descriptor, source),
                GameDataFamily.Agents => ParseAgents(name, descriptor, source),
                GameDataFamily.WantedTargets => ParseWanted(name, descriptor, source),
                GameDataFamily.NewsTicker => ParseFixedIntRecords(name, descriptor, source, 7,
                    ["Active", "Flag0", "Flag1", "Flag2", "Flag3", "Unknown0", "Unknown1"]),
                GameDataFamily.ShipParts => ParseParts(name, descriptor, source, station: false),
                GameDataFamily.StationParts => ParseParts(name, descriptor, source, station: true),
                GameDataFamily.WeaponPositions => ParsePositionGroups(name, descriptor, source),
                _ => ParseOpaque(name, descriptor, source),
            };
        }
        catch (FormatParseException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or DecoderFallbackException)
        {
            throw Failure(name, 0, "document", exception.Message, exception);
        }
    }

    private static GameDataDocument ParseNames(
        string name,
        GameDataFormatDescriptor descriptor,
        byte[] source)
    {
        Cursor cursor = new(name, source);
        int count = cursor.ReadInt32("name count");
        cursor.ValidateCount(count, "name count", 2);
        List<GameDataRecord> records = new(count);
        for (int index = 0; index < count; index++)
        {
            int start = cursor.Offset;
            GameDataField value = cursor.ReadStringField(index, "Name", editable: true, "confirmed");
            records.Add(new GameDataRecord(index, start, cursor.Offset - start, [value]));
        }

        cursor.RequireEnd();
        return Document(name, descriptor, source, records);
    }

    private static GameDataDocument ParseFixedIntRecords(
        string name,
        GameDataFormatDescriptor descriptor,
        byte[] source,
        int integersPerRecord,
        string[] labels)
    {
        int stride = checked(integersPerRecord * 4);
        if (source.Length % stride != 0)
        {
            throw Failure(name, source.Length - source.Length % stride, "record", $"File length is not divisible by the confirmed {stride}-byte record size.");
        }

        Cursor cursor = new(name, source);
        List<GameDataRecord> records = new(source.Length / stride);
        for (int index = 0; cursor.Remaining > 0; index++)
        {
            int start = cursor.Offset;
            List<GameDataField> fields = new(integersPerRecord);
            for (int field = 0; field < integersPerRecord; field++)
            {
                fields.Add(cursor.ReadInt32Field(index, labels[field], editable: true,
                    field == integersPerRecord - 1 && descriptor.Family == GameDataFamily.Ships ? "strong hypothesis" : "structural"));
            }

            records.Add(new GameDataRecord(index, start, stride, fields));
        }

        return Document(name, descriptor, source, records);
    }

    private static GameDataDocument ParseStationRecords(string name, GameDataFormatDescriptor descriptor, byte[] source)
    {
        Cursor cursor = new(name, source);
        List<GameDataRecord> records = [];
        for (int index = 0; cursor.Remaining > 0; index++)
        {
            int start = cursor.Offset;
            List<GameDataField> fields =
            [
                cursor.ReadStringField(index, "Name", true, "confirmed"),
                cursor.ReadInt32Field(index, "StationId", true, "confirmed"),
                cursor.ReadInt32Field(index, "SystemId", true, "confirmed"),
                cursor.ReadInt32Field(index, "TechnologyLevel", true, "confirmed"),
                cursor.ReadInt32Field(index, "PlanetTextureId", true, "confirmed"),
            ];
            records.Add(new GameDataRecord(index, start, cursor.Offset - start, fields));
        }

        return Document(name, descriptor, source, records);
    }

    private static GameDataDocument ParseItems(string name, GameDataFormatDescriptor descriptor, byte[] source)
    {
        Cursor cursor = new(name, source);
        List<GameDataRecord> records = [];
        for (int index = 0; cursor.Remaining > 0; index++)
        {
            int start = cursor.Offset;
            List<GameDataField> fields = [];
            string[] sections = ["BlueprintComponents", "ComponentAmounts", "Attributes"];
            foreach (string section in sections)
            {
                int count = cursor.ReadInt32(section + " count");
                cursor.ValidateCount(count, section + " count", 4);
                for (int element = 0; element < count; element++)
                {
                    fields.Add(cursor.ReadInt32Field(index, $"{section}[{element}]", true, "structurally confirmed"));
                }
            }

            records.Add(new GameDataRecord(index, start, cursor.Offset - start, fields));
        }

        return Document(name, descriptor, source, records,
            ["Array meanings are confirmed by engine consumers; individual attribute semantics remain unresolved."]);
    }

    private static GameDataDocument ParseSystems(string name, GameDataFormatDescriptor descriptor, byte[] source)
    {
        Cursor cursor = new(name, source);
        List<GameDataRecord> records = [];
        string[] fixedLabels =
        [
            "Safety", "VisibleByDefault", "FactionOrRace", "PositionX", "PositionY", "PositionZ",
            "JumpgateStationId", "StarTextureId",
        ];
        for (int index = 0; cursor.Remaining > 0; index++)
        {
            int start = cursor.Offset;
            List<GameDataField> fields = [cursor.ReadStringField(index, "Name", true, "confirmed")];
            fields.AddRange(fixedLabels.Select(label => cursor.ReadInt32Field(index, label, true, "confirmed")));
            foreach ((string label, string confidence) in new[]
            {
                ("StarColor", "confirmed"),
                ("StationIds", "confirmed"),
                ("NeighbourSystemIds", "confirmed"),
                ("LegacyOrStaticIds", "unresolved semantics"),
            })
            {
                int count = cursor.ReadInt32(label + " count");
                cursor.ValidateCount(count, label + " count", 4);
                for (int element = 0; element < count; element++)
                {
                    fields.Add(cursor.ReadInt32Field(index, $"{label}[{element}]", true, confidence));
                }
            }

            records.Add(new GameDataRecord(index, start, cursor.Offset - start, fields));
        }

        return Document(name, descriptor, source, records);
    }

    private static GameDataDocument ParseAgents(string name, GameDataFormatDescriptor descriptor, byte[] source)
    {
        Cursor cursor = new(name, source);
        List<GameDataRecord> records = [];
        string[] commonLabels = ["MessageId", "StationId", "SystemId", "Race", "MaleFlag", "SecretSystemId", "BlueprintId"];
        bool? hasMobileVariantParameter = null;
        for (int index = 0; cursor.Remaining > 0; index++)
        {
            int start = cursor.Offset;
            List<GameDataField> fields = [cursor.ReadStringField(index, "Name", true, "confirmed")];
            fields.AddRange(commonLabels.Select(label => cursor.ReadInt32Field(index, label, true, "confirmed")));
            GameDataField variantOrPrice = cursor.ReadInt32Field(index, "SellPrice", true, "confirmed on PC; variant-dependent on mobile");
            int next = cursor.PeekInt32("face part count or mobile sell price");
            hasMobileVariantParameter ??= next is < 0 or > 255;
            if (hasMobileVariantParameter.Value)
            {
                fields.Add(variantOrPrice with
                {
                    Name = "MobileVariantParameter",
                    Confidence = "structurally confirmed; semantics unresolved",
                });
                fields.Add(cursor.ReadInt32Field(index, "SellPrice", true, "confirmed"));
            }
            else
            {
                fields.Add(variantOrPrice);
            }

            int imageCount = cursor.ReadInt32("face part count");
            cursor.ValidateCount(imageCount, "face part count", 1);
            if (imageCount > 0)
            {
                fields.Add(cursor.ReadRawField(index, "FaceParts", imageCount, editable: true, "structural"));
            }

            records.Add(new GameDataRecord(index, start, cursor.Offset - start, fields));
        }

        return Document(name, descriptor, source, records);
    }

    private static GameDataDocument ParseWanted(string name, GameDataFormatDescriptor descriptor, byte[] source)
    {
        Cursor cursor = new(name, source);
        List<GameDataRecord> records = [];
        string[] labels = ["Id", "ParameterA", "ParameterB", "FlagC", "ParameterD", "ParameterE", "ParameterF", "ParameterG", "ParameterH", "ParameterI", "ParameterJ", "ParameterK", "ParameterL"];
        for (int index = 0; cursor.Remaining > 0; index++)
        {
            int start = cursor.Offset;
            List<GameDataField> fields = [cursor.ReadStringField(index, "Name", true, "confirmed")];
            fields.AddRange(labels.Select(label => cursor.ReadInt32Field(index, label, true,
                label is "Id" or "FlagC" ? "confirmed" : "unresolved semantics")));
            int imageCount = cursor.ReadInt32("image part count");
            if (imageCount is < 0 or > 5)
            {
                throw Failure(name, cursor.Offset - 4, "image part count", $"Observed {imageCount}; expected the confirmed 0..5 range.");
            }

            if (imageCount > 0)
            {
                // The runtime consumes five face-part bytes whenever this flag is nonzero.
                fields.Add(cursor.ReadRawField(index, "FaceParts", 5, true, "confirmed storage, unresolved individual meanings"));
            }

            records.Add(new GameDataRecord(index, start, cursor.Offset - start, fields));
        }

        return Document(name, descriptor, source, records);
    }

    private static GameDataDocument ParseParts(string name, GameDataFormatDescriptor descriptor, byte[] source, bool station)
    {
        Cursor cursor = new(name, source);
        List<GameDataRecord> records = [];
        for (int index = 0; cursor.Remaining > 0; index++)
        {
            int start = cursor.Offset;
            List<GameDataField> fields = [cursor.ReadByteField(index, "GroupId", true, "confirmed")];
            int count;
            if (station)
            {
                fields.Add(cursor.ReadInt16Field(index, "HangarResourceId", true, "confirmed"));
                GameDataField countField = cursor.ReadByteField(index, "AdditionalPartCount", false, "confirmed");
                fields.Add(countField);
                count = byte.Parse(countField.Value, CultureInfo.InvariantCulture);
            }
            else
            {
                GameDataField countField = cursor.ReadByteField(index, "PartCount", false, "confirmed");
                fields.Add(countField);
                count = byte.Parse(countField.Value, CultureInfo.InvariantCulture);
            }

            cursor.ValidateCount(count, "part count", station ? 20 : 26);
            string[] transformLabels = station
                ? ["ResourceId", "PositionX", "PositionY", "PositionZ", "RotationX", "RotationY", "RotationZ"]
                : ["ResourceId", "PositionX", "PositionY", "PositionZ", "RotationX", "RotationY", "RotationZ", "ScaleX", "ScaleY", "ScaleZ"];
            for (int part = 0; part < count; part++)
            {
                foreach (string label in transformLabels)
                {
                    bool wide = label.StartsWith("Position", StringComparison.Ordinal);
                    fields.Add(wide
                        ? cursor.ReadInt32Field(index, $"Part[{part}].{label}", true, "confirmed")
                        : cursor.ReadInt16Field(index, $"Part[{part}].{label}", true, "confirmed"));
                }
            }

            records.Add(new GameDataRecord(index, start, cursor.Offset - start, fields));
        }

        return Document(name, descriptor, source, records);
    }

    private static GameDataDocument ParsePositionGroups(string name, GameDataFormatDescriptor descriptor, byte[] source)
    {
        Cursor cursor = new(name, source);
        List<GameDataRecord> records = [];
        for (int index = 0; cursor.Remaining > 0; index++)
        {
            int start = cursor.Offset;
            List<GameDataField> fields =
            [
                cursor.ReadInt16LittleField(index, "OwnerId", true, "confirmed"),
            ];
            GameDataField countField = cursor.ReadInt16LittleField(index, "PointCount", false, "confirmed");
            fields.Add(countField);
            int count = short.Parse(countField.Value, CultureInfo.InvariantCulture);
            cursor.ValidateCount(count, "point count", 8);
            for (int point = 0; point < count; point++)
            {
                GameDataField type = cursor.ReadInt16LittleField(index, $"Point[{point}].Type", true, "confirmed");
                fields.Add(type);
                fields.Add(cursor.ReadInt16LittleField(index, $"Point[{point}].X", true, "confirmed"));
                fields.Add(cursor.ReadInt16LittleField(index, $"Point[{point}].Y", true, "confirmed"));
                fields.Add(cursor.ReadInt16LittleField(index, $"Point[{point}].Z", true, "confirmed"));
                if (short.Parse(type.Value, CultureInfo.InvariantCulture) == 3)
                {
                    fields.Add(cursor.ReadRawField(index, $"Point[{point}].DirectionFloat3", 12, true,
                        "confirmed float payload; axis semantics provisional"));
                }
            }

            records.Add(new GameDataRecord(index, start, cursor.Offset - start, fields));
        }

        return Document(name, descriptor, source, records);
    }

    private static GameDataDocument ParseOpaque(string name, GameDataFormatDescriptor descriptor, byte[] source)
    {
        GameDataField bytes = new("0:RawBytes", "RawBytes", 0, source.Length, GameDataScalarKind.RawBytes,
            Convert.ToHexString(source), descriptor.SupportLevel == GameDataSupportLevel.LossPreservingAdvancedEditor,
            "opaque; exact byte range only");
        return Document(name, descriptor, source, [new GameDataRecord(0, 0, source.Length, [bytes])],
            ["This family is isolated and byte-preserving. Semantic field meanings are not yet proven."]);
    }

    private static GameDataDocument Document(
        string name,
        GameDataFormatDescriptor descriptor,
        byte[] source,
        IReadOnlyList<GameDataRecord> records,
        IReadOnlyList<string>? warnings = null) =>
        new(name, descriptor.Family, descriptor.SupportLevel, descriptor.Endianness, source, records, warnings ?? []);

    private static GameDataFormatDescriptor Semantic(GameDataFamily family, string name, string endian) =>
        new(family, GameDataSupportLevel.SemanticReadWrite, name, endian, "Confirmed engine filename consumer and observed layout.");

    private static GameDataFormatDescriptor Structural(GameDataFamily family, string name, string endian) =>
        new(family, GameDataSupportLevel.StructuralReadWrite, name, endian, "Confirmed record structure; some field meanings remain unresolved.");

    private static GameDataFormatDescriptor Advanced(GameDataFamily family, string name, string endian) =>
        new(family, GameDataSupportLevel.LossPreservingAdvancedEditor, name, endian, "Recognized consumer; only loss-preserving structural editing is currently safe.");

    private static FormatParseException Failure(string? path, int offset, string field, string reason, Exception? inner = null) =>
        new(FormatFailureKind.Corrupt, path, offset, field, reason, inner);

    private sealed class Cursor(string name, byte[] source)
    {
        public int Offset { get; private set; }

        public int Remaining => source.Length - Offset;

        public int ReadInt32(string field)
        {
            Require(4, field);
            int value = BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(Offset, 4));
            Offset += 4;
            return value;
        }

        public int PeekInt32(string field)
        {
            Require(4, field);
            return BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(Offset, 4));
        }

        public GameDataField ReadInt32Field(int record, string field, bool editable, string confidence)
        {
            int start = Offset;
            int value = ReadInt32(field);
            return Create(record, field, start, 4, GameDataScalarKind.Int32BigEndian,
                value.ToString(CultureInfo.InvariantCulture), editable, confidence);
        }

        public GameDataField ReadInt16Field(int record, string field, bool editable, string confidence)
        {
            int start = Offset;
            Require(2, field);
            short value = BinaryPrimitives.ReadInt16BigEndian(source.AsSpan(Offset, 2));
            Offset += 2;
            return Create(record, field, start, 2, GameDataScalarKind.Int16BigEndian,
                value.ToString(CultureInfo.InvariantCulture), editable, confidence);
        }

        public GameDataField ReadInt16LittleField(int record, string field, bool editable, string confidence)
        {
            int start = Offset;
            Require(2, field);
            short value = BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(Offset, 2));
            Offset += 2;
            return Create(record, field, start, 2, GameDataScalarKind.Int16LittleEndian,
                value.ToString(CultureInfo.InvariantCulture), editable, confidence);
        }

        public GameDataField ReadByteField(int record, string field, bool editable, string confidence)
        {
            int start = Offset;
            Require(1, field);
            byte value = source[Offset++];
            return Create(record, field, start, 1, GameDataScalarKind.UnsignedByte,
                value.ToString(CultureInfo.InvariantCulture), editable, confidence);
        }

        public GameDataField ReadStringField(int record, string field, bool editable, string confidence)
        {
            int start = Offset;
            Require(2, field + " length");
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(Offset, 2));
            Offset += 2;
            Require(length, field);
            string value;
            try
            {
                value = ModifiedUtf8.Decode(source.AsSpan(Offset, length));
            }
            catch (DecoderFallbackException exception)
            {
                throw Failure(name, Offset, field, exception.Message, exception);
            }

            Offset += length;
            return Create(record, field, start, length + 2, GameDataScalarKind.ModifiedUtf8, value, editable, confidence);
        }

        public GameDataField ReadRawField(int record, string field, int length, bool editable, string confidence)
        {
            int start = Offset;
            Require(length, field);
            string value = Convert.ToHexString(source.AsSpan(Offset, length));
            Offset += length;
            return Create(record, field, start, length, GameDataScalarKind.RawBytes, value, editable, confidence);
        }

        public void ValidateCount(int count, string field, int minimumElementBytes)
        {
            if (count < 0 || count > 1_000_000 || (long)count * minimumElementBytes > Remaining)
            {
                throw Failure(name, Math.Max(0, Offset - 4), field,
                    $"Count {count} cannot fit safely in the remaining {Remaining} bytes.");
            }
        }

        public void RequireEnd()
        {
            if (Remaining != 0)
            {
                throw Failure(name, Offset, "trailing bytes", $"Expected end of file; {Remaining} byte(s) remain.");
            }
        }

        private void Require(int length, string field)
        {
            if (length < 0 || length > Remaining)
            {
                throw Failure(name, Offset, field, $"Needs {length} bytes; only {Remaining} remain.");
            }
        }

        private static GameDataField Create(
            int record,
            string name,
            int offset,
            int length,
            GameDataScalarKind kind,
            string value,
            bool editable,
            string confidence) =>
            new($"{record}:{offset}:{name}", name, offset, length, kind, value, editable, confidence);
    }
}

public sealed record GameDataEditOperation(string FieldId, string OriginalValue, string NewValue, string Description);

public sealed record GameDataRecoveryDocument(
    int FormatVersion,
    string SourceHash,
    IReadOnlyList<GameDataEditOperation> Operations);

public sealed class GameDataEditSession
{
    private readonly List<GameDataEditOperation> operations = [];
    private int appliedCount;

    public GameDataEditSession(GameDataDocument original)
    {
        Original = original ?? throw new ArgumentNullException(nameof(original));
    }

    public GameDataDocument Original { get; }

    public IReadOnlyList<GameDataEditOperation> AppliedOperations => operations.Take(appliedCount).ToArray();

    public bool CanUndo => appliedCount > 0;

    public bool CanRedo => appliedCount < operations.Count;

    public void Replace(string fieldId, string newValue)
    {
        GameDataField field = FindField(fieldId);
        if (!field.Editable)
        {
            throw new InvalidOperationException($"Field '{field.Name}' is read-only because its structural effect is not proven safe.");
        }

        ValidateEncodedLength(field, newValue);
        string oldValue = CurrentValue(fieldId);
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            return;
        }

        if (appliedCount < operations.Count)
        {
            operations.RemoveRange(appliedCount, operations.Count - appliedCount);
        }

        operations.Add(new GameDataEditOperation(fieldId, oldValue, newValue, $"Change {field.Name}"));
        appliedCount++;
    }

    public bool Undo()
    {
        if (!CanUndo)
        {
            return false;
        }

        appliedCount--;
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo)
        {
            return false;
        }

        appliedCount++;
        return true;
    }

    public byte[] Write()
    {
        byte[] result = (byte[])Original.OriginalBytes.Clone();
        Dictionary<string, GameDataEditOperation> changes = operations
            .Take(appliedCount)
            .GroupBy(operation => operation.FieldId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        foreach ((string id, GameDataEditOperation operation) in changes)
        {
            GameDataField field = FindField(id);
            byte[] encoded = Encode(field, operation.NewValue);
            encoded.CopyTo(result, field.Offset);
        }

        return result;
    }

    public string SerializeRecovery(string sourceHash) => JsonSerializer.Serialize(
        new GameDataRecoveryDocument(1, sourceHash, operations.Take(appliedCount).ToArray()),
        GameDataJsonContext.Default.GameDataRecoveryDocument);

    public void Replay(GameDataRecoveryDocument recovery, string sourceHash)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        if (recovery.FormatVersion != 1)
        {
            throw new InvalidDataException($"Unsupported game-data recovery version {recovery.FormatVersion}.");
        }

        if (!string.Equals(recovery.SourceHash, sourceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The source hash changed; recovery was not replayed.");
        }

        foreach (GameDataEditOperation operation in recovery.Operations)
        {
            Replace(operation.FieldId, operation.NewValue);
        }
    }

    private string CurrentValue(string id)
    {
        string result = FindField(id).Value;
        foreach (GameDataEditOperation operation in operations.Take(appliedCount))
        {
            if (string.Equals(operation.FieldId, id, StringComparison.Ordinal))
            {
                result = operation.NewValue;
            }
        }

        return result;
    }

    private GameDataField FindField(string id) => Original.Records
        .SelectMany(record => record.Fields)
        .FirstOrDefault(field => string.Equals(field.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Game-data field '{id}' is not present.");

    private static void ValidateEncodedLength(GameDataField field, string value)
    {
        byte[] encoded = Encode(field, value);
        if (encoded.Length != field.Length)
        {
            throw new InvalidOperationException(
                $"Changing '{field.Name}' would resize its record ({field.Length} -> {encoded.Length} bytes). " +
                "This loss-preserving editor only permits size-stable edits for this family.");
        }
    }

    private static byte[] Encode(GameDataField field, string value)
    {
        byte[] bytes = new byte[field.Length];
        switch (field.Kind)
        {
            case GameDataScalarKind.Int32BigEndian:
                BinaryPrimitives.WriteInt32BigEndian(bytes, int.Parse(value, CultureInfo.InvariantCulture));
                break;
            case GameDataScalarKind.Int16BigEndian:
                BinaryPrimitives.WriteInt16BigEndian(bytes, short.Parse(value, CultureInfo.InvariantCulture));
                break;
            case GameDataScalarKind.Int16LittleEndian:
                BinaryPrimitives.WriteInt16LittleEndian(bytes, short.Parse(value, CultureInfo.InvariantCulture));
                break;
            case GameDataScalarKind.UnsignedByte:
                bytes[0] = byte.Parse(value, CultureInfo.InvariantCulture);
                break;
            case GameDataScalarKind.ModifiedUtf8:
                byte[] text = ModifiedUtf8.Encode(value);
                if (text.Length > ushort.MaxValue)
                {
                    throw new InvalidOperationException("Modified UTF-8 values cannot exceed 65,535 bytes.");
                }

                bytes = new byte[text.Length + 2];
                BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)text.Length));
                text.CopyTo(bytes, 2);
                break;
            case GameDataScalarKind.RawBytes:
                bytes = Convert.FromHexString(value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field.Kind, "Unknown scalar kind.");
        }

        return bytes;
    }
}

internal static class ModifiedUtf8
{
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        StringBuilder result = new(bytes.Length);
        for (int index = 0; index < bytes.Length;)
        {
            byte first = bytes[index++];
            if ((first & 0x80) == 0)
            {
                if (first == 0)
                {
                    throw new DecoderFallbackException("Modified UTF-8 uses C0 80 rather than a zero byte.");
                }

                result.Append((char)first);
            }
            else if ((first & 0xE0) == 0xC0)
            {
                if (index >= bytes.Length || (bytes[index] & 0xC0) != 0x80)
                {
                    throw new DecoderFallbackException("Invalid two-byte modified UTF-8 sequence.");
                }

                int value = ((first & 0x1F) << 6) | (bytes[index++] & 0x3F);
                result.Append((char)value);
            }
            else if ((first & 0xF0) == 0xE0)
            {
                if (index + 1 >= bytes.Length || (bytes[index] & 0xC0) != 0x80 || (bytes[index + 1] & 0xC0) != 0x80)
                {
                    throw new DecoderFallbackException("Invalid three-byte modified UTF-8 sequence.");
                }

                int value = ((first & 0x0F) << 12) | ((bytes[index++] & 0x3F) << 6) | (bytes[index++] & 0x3F);
                result.Append((char)value);
            }
            else
            {
                throw new DecoderFallbackException("Modified UTF-8 stores UTF-16 code units in at most three bytes.");
            }
        }

        return result.ToString();
    }

    public static byte[] Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using MemoryStream output = new();
        foreach (char character in value)
        {
            if (character is >= '\u0001' and <= '\u007F')
            {
                output.WriteByte((byte)character);
            }
            else if (character <= '\u07FF')
            {
                output.WriteByte((byte)(0xC0 | ((character >> 6) & 0x1F)));
                output.WriteByte((byte)(0x80 | (character & 0x3F)));
            }
            else
            {
                output.WriteByte((byte)(0xE0 | ((character >> 12) & 0x0F)));
                output.WriteByte((byte)(0x80 | ((character >> 6) & 0x3F)));
                output.WriteByte((byte)(0x80 | (character & 0x3F)));
            }
        }

        return output.ToArray();
    }
}

[JsonSerializable(typeof(GameDataRecoveryDocument))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class GameDataJsonContext : JsonSerializerContext;
