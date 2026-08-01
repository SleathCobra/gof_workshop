using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Gof2Workshop.App.Presentation;
using Gof2Workshop.GameData;
using Gof2Workshop.Workbench;

namespace Gof2Workshop.App.Documents;

public sealed record GameDataFieldRow(
    int RecordIndex,
    GameDataField Field,
    string Value)
{
    public string Offset => $"0x{Field.Offset:X8}";

    public string Length => Field.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string EditStatus => Field.Editable ? "Safe" : "Read only";
}

public sealed class GameDataDocumentViewModel :
    DocumentViewModelBase,
    IExportableDocument,
    IUndoableDocument
{
    private readonly IndexedAsset asset;
    private readonly GameDataDocument document;
    private readonly GameDataEditSession session;
    private readonly WorkspaceDefinition workspace;
    private readonly IUserDialogService dialogs;
    private readonly IOutputService output;
    private readonly IProblemService problems;
    private GameDataFieldRow? selectedField;
    private string editValue = string.Empty;
    private string searchText = string.Empty;

    public GameDataDocumentViewModel(
        IndexedAsset asset,
        GameDataDocument document,
        WorkspaceDefinition workspace,
        IUserDialogService dialogs,
        IOutputService output,
        IProblemService problems)
        : base(
            DocumentManager.NormalizeDocumentId(asset.FullPath),
            asset.FileName,
            "Structured BIN",
            asset.FullPath,
            asset.Ownership == AssetOwnership.Game)
    {
        this.asset = asset;
        this.document = document;
        this.workspace = workspace;
        this.dialogs = dialogs;
        this.output = output;
        this.problems = problems;
        session = new GameDataEditSession(document);
        ApplyCommand = new RelayCommand(Apply, CanApply);
        UndoCommand = new RelayCommand(Undo, () => session.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => session.CanRedo);
        ExportCommand = new AsyncRelayCommand(ExportDefaultAsync);
        RefreshRows();
    }

    public ObservableCollection<GameDataFieldRow> Fields { get; } = [];

    public GameDataFieldRow? SelectedField
    {
        get => selectedField;
        set
        {
            if (SetProperty(ref selectedField, value))
            {
                EditValue = value?.Value ?? string.Empty;
                ApplyCommand.RaiseCanExecuteChanged();
                RaiseInspectorChanged();
            }
        }
    }

    public string EditValue
    {
        get => editValue;
        set
        {
            if (SetProperty(ref editValue, value))
            {
                ApplyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value))
            {
                RefreshRows(SelectedField?.Field.Id);
            }
        }
    }

    public RelayCommand ApplyCommand { get; }

    public System.Windows.Input.ICommand UndoCommand { get; }

    public System.Windows.Input.ICommand RedoCommand { get; }

    public AsyncRelayCommand ExportCommand { get; }

    public string Summary =>
        $"{document.Family} · {document.SupportLevel} · {document.Records.Count:N0} records · " +
        $"{document.EditableFieldCount:N0} structurally safe fields";

    public string SafetyMessage => IsReadOnly
        ? "Original game data is read-only. Add this BIN to the mod workspace before editing."
        : "Unknown bytes are preserved. Size-changing edits are refused.";

    public override IReadOnlyList<InspectorGroup> InspectorGroups
    {
        get
        {
            List<InspectorGroup> groups =
            [
                new(
                    "Structured data",
                    [
                        new InspectorProperty("Family", document.Family.ToString()),
                        new InspectorProperty("Support", document.SupportLevel.ToString()),
                        new InspectorProperty("Records", document.Records.Count.ToString("N0", CultureInfo.InvariantCulture)),
                        new InspectorProperty("Editable fields", document.EditableFieldCount.ToString("N0", CultureInfo.InvariantCulture)),
                        new InspectorProperty("Endianness", document.Endianness),
                    ]),
            ];
            if (SelectedField is { } selected)
            {
                groups.Add(new InspectorGroup(
                    "Selected field",
                    [
                        new InspectorProperty("Record", selected.RecordIndex.ToString(CultureInfo.InvariantCulture)),
                        new InspectorProperty("Name", selected.Field.Name),
                        new InspectorProperty("Type", selected.Field.Kind.ToString()),
                        new InspectorProperty("Offset", selected.Offset),
                        new InspectorProperty("Length", selected.Length),
                        new InspectorProperty("Confidence", selected.Field.Confidence),
                        new InspectorProperty("Edit safety", selected.EditStatus),
                    ],
                    IsAdvanced: true));
            }

            return groups;
        }
    }

    public override string AssetDetails => JsonSerializer.Serialize(
        new
        {
            document.Name,
            family = document.Family,
            support = document.SupportLevel,
            document.Endianness,
            records = document.Records.Count,
            editableFields = document.EditableFieldCount,
            warnings = document.Warnings,
            operations = session.AppliedOperations,
        },
        DetailsJsonOptions);

    public async Task ExportDefaultAsync()
    {
        string? destination = await dialogs.SaveFileAsync(
            "Export validated structured BIN copy",
            Path.GetFileNameWithoutExtension(asset.FileName) + "-working.bin",
            ".bin",
            Path.GetDirectoryName(asset.FullPath));
        if (destination is null)
        {
            return;
        }

        if (workspace.GameAssetRoot is string gameRoot && PathPolicy.IsWithin(destination, gameRoot))
        {
            throw new InvalidOperationException("Structured-data exports cannot be written beneath the immutable game root.");
        }

        byte[] bytes = session.Write();
        GameDataDocument reparsed = new GameDataFormatRegistry().Parse(asset.FileName, bytes);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        output.Write(
            OutputLevel.Information,
            "Structured data",
            $"Exported {Path.GetFileName(destination)}; {reparsed.Records.Count:N0} records reparsed and unknown bytes retained.");
    }

    private bool CanApply() => !IsReadOnly && SelectedField?.Field.Editable == true;

    private void Apply()
    {
        try
        {
            if (SelectedField is null)
            {
                return;
            }

            string fieldId = SelectedField.Field.Id;
            session.Replace(fieldId, EditValue);
            byte[] outputBytes = session.Write();
            _ = new GameDataFormatRegistry().Parse(asset.FileName, outputBytes);
            RefreshRows(fieldId);
            output.Write(OutputLevel.Information, "Structured data", $"Changed {SelectedField?.Field.Name}; writer reparse passed.");
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or InvalidOperationException or OverflowException)
        {
            problems.Add(ProblemEntry.Error(asset, exception.Message, "Use a value that preserves the field's encoded size and range."));
        }
    }

    private void Undo()
    {
        string? fieldId = SelectedField?.Field.Id;
        session.Undo();
        RefreshRows(fieldId);
    }

    private void Redo()
    {
        string? fieldId = SelectedField?.Field.Id;
        session.Redo();
        RefreshRows(fieldId);
    }

    private void RefreshRows(string? selectedId = null)
    {
        Dictionary<string, string> current = session.AppliedOperations
            .GroupBy(operation => operation.FieldId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().NewValue, StringComparer.Ordinal);
        IEnumerable<GameDataFieldRow> rows = document.Records.SelectMany(record => record.Fields.Select(field =>
            new GameDataFieldRow(record.Index, field, current.GetValueOrDefault(field.Id, field.Value))));
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            rows = rows.Where(row =>
                row.Field.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                row.Value.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                row.RecordIndex.ToString(CultureInfo.InvariantCulture).Contains(SearchText, StringComparison.Ordinal));
        }

        Fields.Clear();
        foreach (GameDataFieldRow row in rows.Take(100_000))
        {
            Fields.Add(row);
        }

        SelectedField = selectedId is null
            ? null
            : Fields.FirstOrDefault(row => string.Equals(row.Field.Id, selectedId, StringComparison.Ordinal));
        ((RelayCommand)UndoCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RedoCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(AssetDetails));
        RaiseInspectorChanged();
    }
}
