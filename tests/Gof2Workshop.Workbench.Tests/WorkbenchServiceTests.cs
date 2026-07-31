using System.Text;
using Gof2Workshop.Core;
using Gof2Workshop.Formats.Aei;

namespace Gof2Workshop.Workbench.Tests;

[TestClass]
public sealed class WorkbenchServiceTests
{
    private string temporaryRoot = null!;

    [TestMethod]
    public void PlatformProfilesAreExplicitAndGof3DIsIsolated()
    {
        string[] expected =
        [
            "gof2-pc-1x",
            "gof2-android",
            "gof2-ios",
            "gof2-macos",
            "gof3d-ios-research",
        ];

        CollectionAssert.AreEqual(expected, ProfileCatalog.All.Select(profile => profile.Id).ToArray());
        Assert.AreEqual(AssetProduct.GalaxyOnFire3D, ProfileCatalog.Gof3DIosResearch.Details.Product);
        Assert.AreEqual(ProfileSupportLevel.ResearchReadOnly, ProfileCatalog.Gof3DIosResearch.Details.AemReadSupport);
        Assert.AreEqual(ProfileSupportLevel.Unsupported, ProfileCatalog.Gof3DIosResearch.Details.AemWriteSupport);
        Assert.AreSame(ProfileCatalog.Pc1X, ProfileCatalog.Resolve("pc-1x"));
        Assert.AreSame(ProfileCatalog.Android, ProfileCatalog.Resolve("android"));
    }

    [TestInitialize]
    public void Initialize()
    {
        temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "Gof2WorkshopTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        string resolved = Path.GetFullPath(temporaryRoot);
        string expectedParent = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "Gof2WorkshopTests"));
        if (Directory.Exists(resolved) && PathPolicy.IsWithin(resolved, expectedParent))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }

    [TestMethod]
    public async Task WorkspaceRoundTripsAndCreatesOwnedFolders()
    {
        WorkspaceService service = new();
        WorkspaceDefinition workspace = await service.CreateAsync(
            temporaryRoot,
            "Synthetic Mod",
            ProfileCatalog.Pc1X.Id);
        workspace.GameAssetRoot = Path.Combine(temporaryRoot, "external-assets");
        workspace.Layout.ExplorerWidth = 411;
        workspace.OpenDocuments.Add(new WorkspaceDocumentState("textures/test.aei", "AEI Texture"));
        await service.SaveAsync(workspace);

        WorkspaceLoadResult loaded = await service.LoadAsync(workspace.FilePath!);

        Assert.AreEqual("Synthetic Mod", loaded.Workspace.Name);
        Assert.AreEqual(ProfileCatalog.Pc1X.Id, loaded.Workspace.ProfileId);
        Assert.AreEqual(411, loaded.Workspace.Layout.ExplorerWidth);
        Assert.HasCount(1, loaded.Workspace.OpenDocuments);
        Assert.IsTrue(Directory.Exists(Path.Combine(temporaryRoot, "Assets", "Textures")));
        Assert.IsTrue(Directory.Exists(Path.Combine(temporaryRoot, "Assets", "Models")));
        Assert.IsTrue(Directory.Exists(Path.Combine(temporaryRoot, "Generated")));
        Assert.IsTrue(loaded.Warnings.Any(value => value.Contains("game asset", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task WorkspaceLoadResolvesRelativeGameRootFromWorkspaceDirectory()
    {
        string assetRoot = Path.Combine(temporaryRoot, "Assets");
        Directory.CreateDirectory(assetRoot);
        string workspacePath = Path.Combine(temporaryRoot, "project.gof2workspace");
        await File.WriteAllTextAsync(
            workspacePath,
            """
            {
              "formatVersion": 1,
              "name": "Relative root",
              "profileId": "pc-1x",
              "gameAssetRoot": "Assets",
              "modRoot": ".",
              "outputRoot": "Generated"
            }
            """);

        WorkspaceLoadResult result = await new WorkspaceService().LoadAsync(workspacePath);

        Assert.AreEqual(Path.GetFullPath(assetRoot), result.Workspace.GameAssetRoot);
        Assert.IsFalse(result.Warnings.Any(value =>
            value.Contains("game asset folder is missing", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task VersionZeroWorkspaceMigratesWithoutLosingProfile()
    {
        string path = Path.Combine(temporaryRoot, "legacy.gof2workspace");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "formatVersion": 0,
              "name": "Legacy",
              "profileId": "android",
              "modRoot": ".",
              "outputRoot": "Generated"
            }
            """);

        WorkspaceLoadResult result = await new WorkspaceService().LoadAsync(path);

        Assert.AreEqual(WorkspaceDefinition.CurrentFormatVersion, result.Workspace.FormatVersion);
        Assert.AreEqual("android", result.Workspace.ProfileId);
        Assert.IsTrue(result.Warnings.Any(value => value.Contains("migrated", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task WorkspaceServiceRefusesToSaveConfigurationBeneathGameRoot()
    {
        string gameRoot = Path.Combine(temporaryRoot, "game");
        Directory.CreateDirectory(gameRoot);
        WorkspaceDefinition workspace = new()
        {
            Name = "Unsafe",
            ProfileId = "pc-1x",
            GameAssetRoot = gameRoot,
            FilePath = Path.Combine(gameRoot, "project.gof2workspace"),
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => new WorkspaceService().SaveAsync(workspace));
    }

    [TestMethod]
    public async Task MalformedApplicationStateFallsBackToDefaults()
    {
        string path = Path.Combine(temporaryRoot, "application-state.json");
        await File.WriteAllTextAsync(path, "{ this is malformed");

        ApplicationStateLoadResult result = await new ApplicationStateService(path).LoadAsync();

        Assert.IsNotNull(result.Warning);
        Assert.AreEqual(ApplicationState.CurrentFormatVersion, result.State.FormatVersion);
        Assert.AreEqual(1500, result.State.Window.Width);
    }

    [TestMethod]
    public void LayoutStateNormalizesNonFiniteAndUnusableSizes()
    {
        WorkbenchLayoutState layout = new()
        {
            ExplorerWidth = double.NaN,
            InspectorWidth = -50,
            BottomHeight = 100_000,
            ActiveActivity = string.Empty,
        };

        layout.Normalize();

        Assert.AreEqual(300, layout.ExplorerWidth);
        Assert.AreEqual(220, layout.InspectorWidth);
        Assert.AreEqual(600, layout.BottomHeight);
        Assert.AreEqual("Explorer", layout.ActiveActivity);
    }

    [TestMethod]
    public void ExportPolicyRejectsGameRootAndDescendantsButNotPrefixSibling()
    {
        string gameRoot = Path.Combine(temporaryRoot, "game");
        string descendant = Path.Combine(gameRoot, "textures", "copy.png");
        string sibling = Path.Combine(temporaryRoot, "game-mod", "copy.png");

        _ = Assert.Throws<InvalidOperationException>(
            () => PathPolicy.ValidateExportDestination(gameRoot, gameRoot));
        _ = Assert.Throws<InvalidOperationException>(
            () => PathPolicy.ValidateExportDestination(descendant, gameRoot));
        Assert.AreEqual(
            Path.GetFullPath(sibling),
            PathPolicy.ValidateExportDestination(sibling, gameRoot));
    }

    [TestMethod]
    public async Task AssetIndexClassifiesSupportedAndRecognizedUnsupportedHeaders()
    {
        string root = Path.Combine(temporaryRoot, "assets");
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        Directory.CreateDirectory(Path.Combine(root, "models"));
        await File.WriteAllBytesAsync(
            Path.Combine(root, "textures", "dxt5.aei"),
            CreateAeiHeader(0x24));
        await File.WriteAllBytesAsync(
            Path.Combine(root, "textures", "etc1.aei"),
            CreateAeiHeader(0x40));
        await File.WriteAllBytesAsync(
            Path.Combine(root, "models", "ship.aem"),
            CreateAemHeader("V5AEMesh", 0x17));
        await File.WriteAllBytesAsync(
            Path.Combine(root, "models", "legacy.aem"),
            CreateAemHeader("V2AEMesh", 0x17));

        AssetIndexResult result = await new AssetIndexService().ScanAsync(
            root,
            AssetOwnership.Game,
            ProfileCatalog.Pc1X);

        Assert.HasCount(4, result.Assets);
        Assert.HasCount(
            4,
            result.Assets.Where(asset => asset.Support == AssetSupport.Supported));
        Assert.HasCount(1, result.Problems);
        StringAssert.Contains(result.Problems[0].Message, "outside the expected");
    }

    [TestMethod]
    public async Task QuickInspectIndexesStandaloneAssetsAndKeepsCompanionsReadOnly()
    {
        string aeiPath = Path.Combine(temporaryRoot, "texture.aei");
        string aemPath = Path.Combine(temporaryRoot, "model.aem");
        string languagePath = Path.Combine(temporaryRoot, "english.lang");
        string pngPath = Path.Combine(temporaryRoot, "texture.png");
        await File.WriteAllBytesAsync(aeiPath, CreateAeiHeader(0x24));
        await File.WriteAllBytesAsync(aemPath, CreateAemHeader("V4AEMesh", 0x17));
        await File.WriteAllBytesAsync(languagePath, [0, 1, (byte)'A']);
        await File.WriteAllBytesAsync(pngPath, [1, 2, 3]);
        InspectionCollection collection = new(ProfileCatalog.Pc1X);

        InspectionCollectionUpdate update = await collection.AddAsync(
            [aeiPath, aemPath, languagePath, pngPath, aeiPath]);

        Assert.HasCount(3, update.AddedAssets);
        Assert.HasCount(3, collection.Assets);
        Assert.HasCount(1, collection.Assets.Where(asset => asset.Kind == AssetKind.Language));
        Assert.HasCount(1, collection.CompanionFiles);
        Assert.IsTrue(collection.Assets.All(asset => asset.Ownership == AssetOwnership.Game));
        WorkspaceDefinition transient = collection.CreateTransientWorkspace();
        Assert.IsNull(transient.FilePath);
        Assert.IsNull(transient.GameAssetRoot);
        Assert.AreEqual(ProfileCatalog.Pc1X.Id, transient.ProfileId);
    }

    [TestMethod]
    public async Task AssetIndexReportsAddedRemovedAndChangedAcrossRescan()
    {
        string root = Path.Combine(temporaryRoot, "assets");
        Directory.CreateDirectory(root);
        string first = Path.Combine(root, "first.aei");
        await File.WriteAllBytesAsync(first, CreateAeiHeader(0x20));
        AssetIndexService index = new();

        AssetIndexResult initial = await index.ScanAsync(
            root,
            AssetOwnership.Game,
            ProfileCatalog.Pc1X);
        await File.WriteAllBytesAsync(first, CreateAeiHeader(0x24).Concat(new byte[] { 0 }).ToArray());
        string second = Path.Combine(root, "second.aem");
        await File.WriteAllBytesAsync(second, CreateAemHeader("V4AEMesh", 0x17));
        AssetIndexResult updated = await index.ScanAsync(
            root,
            AssetOwnership.Game,
            ProfileCatalog.Pc1X);
        File.Delete(first);
        AssetIndexResult final = await index.ScanAsync(
            root,
            AssetOwnership.Game,
            ProfileCatalog.Pc1X);

        Assert.AreEqual(1, initial.Delta.Added);
        Assert.AreEqual(1, updated.Delta.Added);
        Assert.AreEqual(1, updated.Delta.Changed);
        Assert.AreEqual(1, final.Delta.Removed);
    }

    [TestMethod]
    public void SearchFiltersByNameKindSupportAndVersion()
    {
        IndexedAsset[] assets =
        [
            CreateIndexed("ships/fighter.aem", AssetKind.Aem, "4", AssetSupport.Supported),
            CreateIndexed("textures/fighter_diffuse.aei", AssetKind.Aei, "0x24", AssetSupport.Supported),
            CreateIndexed("textures/fighter_mobile.aei", AssetKind.Aei, "0x10", AssetSupport.RecognizedUnsupported),
        ];

        IReadOnlyList<IndexedAsset> results = AssetSearchService.Search(
            assets,
            new AssetSearchQuery(
                "fighter",
                AssetKind.Aei,
                AssetSupport.RecognizedUnsupported,
                "0x10"));

        Assert.HasCount(1, results);
        Assert.AreEqual("fighter_mobile.aei", results[0].FileName);
    }

    [TestMethod]
    public async Task PreCancelledAssetScanStopsBeforeWork()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => new AssetIndexService().ScanAsync(
                temporaryRoot,
                AssetOwnership.Game,
                ProfileCatalog.Pc1X,
                cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public void MaterialResolutionFeedsUsesAndReferencedByGraphWithoutClaimingGameWrite()
    {
        IndexedAsset model = CreateIndexed(
            "meshes/fighter_lod_0.aem",
            AssetKind.Aem,
            "v4",
            AssetSupport.Supported);
        IndexedAsset texture = CreateIndexed(
            "textures/fighter_diffuse.aei",
            AssetKind.Aei,
            "0x24",
            AssetSupport.Supported);
        AssetRelationshipService service = new();
        service.UpdateAssets([model, texture]);

        AssetRelationshipResolution resolution = service.ResolveMaterial(
            new WorkspaceDefinition(),
            model,
            0);

        Assert.AreSame(texture, resolution.SelectedAsset);
        IReadOnlyList<AssetDependency> uses = service.GetUses(model);
        Assert.HasCount(1, uses);
        AssetDependency use = uses[0];
        Assert.AreEqual(AssetDependencyKind.MaterialTexture, use.Kind);
        Assert.AreEqual(AssetDependencyEffect.HeuristicGameMapping, use.Effect);
        IReadOnlyList<AssetDependency> references = service.GetReferencedBy(texture);
        Assert.HasCount(1, references);
        Assert.AreSame(model, references[0].Source);
    }

    [TestMethod]
    public void ManualMaterialMappingIsExplicitlyViewerOnly()
    {
        IndexedAsset model = CreateIndexed("meshes/custom.aem", AssetKind.Aem, "v4", AssetSupport.Supported);
        IndexedAsset texture = CreateIndexed("textures/paint.aei", AssetKind.Aei, "0x24", AssetSupport.Supported);
        WorkspaceDefinition workspace = new() { GameAssetRoot = temporaryRoot };
        AssetRelationshipService service = new();
        service.UpdateAssets([texture]);
        service.SetMaterialOverride(workspace, model, 1, texture);

        _ = service.ResolveMaterial(workspace, model, 1);

        IReadOnlyList<AssetDependency> dependencies = service.GetUses(model);
        Assert.HasCount(1, dependencies);
        AssetDependency dependency = dependencies[0];
        Assert.AreEqual(AssetDependencyEffect.ViewerOnly, dependency.Effect);
        Assert.AreEqual(AssetRelationshipConfidence.Confirmed, dependency.Confidence);
    }

    [TestMethod]
    public void ProblemAggregationPreservesStructuredFieldsAndClears()
    {
        ProblemService service = new();
        int changes = 0;
        service.Changed += (_, _) => changes++;
        ProblemEntry first = new(
            ProblemSeverity.Error,
            "sample.aei",
            "sample.aei",
            "DXT5",
            "Truncated payload",
            42,
            "payload",
            "Restore the original file.");

        service.Add(first);
        service.AddRange(
        [
            new ProblemEntry(
                ProblemSeverity.Warning,
                "legacy.aem",
                "legacy.aem",
                "v2",
                "Unsupported version",
                null,
                "signature",
                null),
        ]);

        Assert.HasCount(2, service.Entries);
        Assert.AreEqual(42, service.Entries[0].Offset);
        service.Clear();
        Assert.IsEmpty(service.Entries);
        Assert.AreEqual(3, changes);
    }

    [TestMethod]
    public async Task ProviderResolutionAndDocumentManagerDeduplicateByPath()
    {
        DocumentEditorRegistry registry = new();
        FakeProvider provider = new();
        registry.Register(provider);
        using DocumentManager manager = new(registry);
        WorkspaceDefinition workspace = new();
        IndexedAsset asset = CreateIndexed("textures/test.aei", AssetKind.Aei, "0x24", AssetSupport.Supported);

        IDocument first = await manager.OpenAsync(asset, workspace);
        IDocument second = await manager.OpenAsync(asset with
        {
            FullPath = asset.FullPath.ToUpperInvariant(),
        }, workspace);

        Assert.AreSame(first, second);
        Assert.HasCount(1, manager.Documents);
        Assert.AreEqual(1, provider.OpenCount);
        WorkspaceDocumentState active = manager.CaptureActiveState(temporaryRoot)
            ?? throw new AssertFailedException("Active state was not captured.");
        Assert.AreEqual(Path.Combine("textures", "test.aei"), active.AssetPath);
    }

    [TestMethod]
    public async Task ConcurrentDocumentOpensShareOneProviderOperation()
    {
        DocumentEditorRegistry registry = new();
        DelayedProvider provider = new();
        registry.Register(provider);
        using DocumentManager manager = new(registry);
        WorkspaceDefinition workspace = new();
        IndexedAsset asset = CreateIndexed(
            "meshes/concurrent.aem",
            AssetKind.Aem,
            "v4",
            AssetSupport.Supported);

        Task<IDocument> firstOpen = manager.OpenAsync(asset, workspace);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<IDocument> secondOpen = manager.OpenAsync(
            asset with { FullPath = asset.FullPath.ToUpperInvariant() },
            workspace);
        provider.Release.TrySetResult();

        IDocument[] opened = await Task.WhenAll(firstOpen, secondOpen);

        Assert.AreSame(opened[0], opened[1]);
        Assert.HasCount(1, manager.Documents);
        Assert.AreEqual(1, provider.OpenCount);
    }

    [TestMethod]
    public void DocumentManagerClosesOtherAndRightHandDocumentsAtomically()
    {
        DocumentEditorRegistry registry = new();
        using DocumentManager manager = new(registry);
        FakeDocument first = new(
            CreateIndexed("meshes/first.aem", AssetKind.Aem, "v4", AssetSupport.Supported));
        FakeDocument second = new(
            CreateIndexed("meshes/second.aem", AssetKind.Aem, "v4", AssetSupport.Supported));
        FakeDocument third = new(
            CreateIndexed("meshes/third.aem", AssetKind.Aem, "v4", AssetSupport.Supported));
        manager.Add(first);
        manager.Add(second);
        manager.Add(third);

        Assert.AreEqual(1, manager.CloseToRight(second));
        CollectionAssert.AreEqual(
            new IDocument[] { first, second },
            manager.Documents.ToArray());
        Assert.AreSame(second, manager.ActiveDocument);

        Assert.AreEqual(1, manager.CloseOthers(second));
        CollectionAssert.AreEqual(
            new IDocument[] { second },
            manager.Documents.ToArray());
        Assert.AreSame(second, manager.ActiveDocument);
    }

    [TestMethod]
    public async Task RecentDocumentRestorationSkipsMissingAssets()
    {
        string gameRoot = Path.Combine(temporaryRoot, "game");
        Directory.CreateDirectory(Path.Combine(gameRoot, "textures"));
        string path = Path.Combine(gameRoot, "textures", "restored.aei");
        await File.WriteAllBytesAsync(path, CreateAeiHeader(0x24));
        IndexedAsset available = CreateIndexed(
            Path.GetRelativePath(gameRoot, path),
            AssetKind.Aei,
            "0x24",
            AssetSupport.Supported) with
        {
            FullPath = path,
        };
        WorkspaceDefinition workspace = new() { GameAssetRoot = gameRoot };
        DocumentEditorRegistry registry = new();
        registry.Register(new FakeProvider());
        using DocumentManager manager = new(registry);

        await manager.RestoreAsync(
        [
            new WorkspaceDocumentState("textures/restored.aei", "AEI Texture"),
            new WorkspaceDocumentState("textures/missing.aei", "AEI Texture"),
        ],
        [available],
        workspace);

        Assert.HasCount(1, manager.Documents);
        Assert.AreEqual(path, manager.Documents[0].SourcePath);
    }

    [TestMethod]
    public async Task AddToModStagesValidatedCopyAndAuditedReplacementOutsideGameRoot()
    {
        string gameRoot = Path.Combine(temporaryRoot, "game");
        string modRoot = Path.Combine(temporaryRoot, "mod");
        Directory.CreateDirectory(Path.Combine(gameRoot, "textures"));
        string sourcePath = Path.Combine(gameRoot, "textures", "sample.aei");
        byte[] original = CreateRawAei(255, 0, 0, 255);
        await File.WriteAllBytesAsync(sourcePath, original);
        WorkspaceService workspaceService = new();
        WorkspaceDefinition workspace = await workspaceService.CreateAsync(
            modRoot,
            "Staging",
            ProfileCatalog.Pc1X.Id);
        workspace.GameAssetRoot = gameRoot;
        IndexedAsset source = new(
            sourcePath,
            Path.Combine("textures", "sample.aei"),
            "sample.aei",
            AssetKind.Aei,
            AssetOwnership.Game,
            original.Length,
            DateTimeOffset.UtcNow,
            "Raw RGBA UI",
            "0x01",
            AssetSupport.Supported,
            true,
            null);
        ModStagingService staging = new(workspaceService);

        ModStagingResult added = await staging.AddOriginalAsync(workspace, source);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(added.StagedPath));
        Assert.IsFalse(PathPolicy.IsWithin(added.StagedPath, gameRoot));

        string replacement = Path.Combine(temporaryRoot, "replacement.aei");
        byte[] blue = CreateRawAei(0, 0, 255, 255);
        await File.WriteAllBytesAsync(replacement, blue);
        ModStagingResult replaced = await staging.StageReplacementAsync(
            workspace,
            source,
            replacement,
            overwrite: true);

        CollectionAssert.AreEqual(blue, await File.ReadAllBytesAsync(replaced.StagedPath));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(sourcePath));
        string manifest = Path.Combine(modRoot, ".work", "asset-operations.json");
        StringAssert.Contains(await File.ReadAllTextAsync(manifest), "\"Replace\"");

        workspace.ModId = "tests.synthetic";
        workspace.Author = "Workshop tests";
        ModBuildService buildService = new(workspaceService);
        ModBuildResult firstBuild = await buildService.BuildAsync(workspace);
        ModBuildResult secondBuild = await buildService.BuildAsync(workspace);
        Assert.AreEqual(firstBuild.Report.ContentSha256, secondBuild.Report.ContentSha256);
        Assert.HasCount(1, secondBuild.Report.Assets);
        Assert.IsTrue(File.Exists(secondBuild.ManifestPath));
        Assert.IsTrue(File.Exists(Path.Combine(
            secondBuild.OutputDirectory,
            "Assets",
            "textures",
            "sample.aei")));

        await File.WriteAllBytesAsync(sourcePath, CreateRawAei(1, 2, 3, 255));
        ModValidationResult conflict = await buildService.ValidateAsync(workspace);
        Assert.IsFalse(conflict.IsValid);
        Assert.IsTrue(conflict.Issues.Any(issue =>
            issue.Severity == ModValidationSeverity.Error
            && issue.Message.Contains("source hash", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void AeiEditSessionSupportsUndoRedoDivergenceAndValidation()
    {
        AeiFile file = ParseEditableAei();
        RgbaImage original = new AeiTextureDecoder().DecodeAtlas(file);
        AeiEditSession session = new(
            "textures/editable.aei",
            new string('a', 64),
            "Assets/Textures/textures/editable.aei",
            file,
            original);
        RgbaImage green = SolidImage(1, 1, new Rgba32(0, 255, 0, 255));
        RgbaImage blue = SolidImage(1, 1, new Rgba32(0, 0, 255, 255));

        session.ReplaceRegion(0, green);
        Assert.AreEqual(new Rgba32(0, 255, 0, 255), session.WorkingAtlas.GetPixel(0, 0));
        session.Undo();
        Assert.AreEqual(new Rgba32(255, 0, 0, 255), session.WorkingAtlas.GetPixel(0, 0));
        session.Redo();
        Assert.AreEqual(new Rgba32(0, 255, 0, 255), session.WorkingAtlas.GetPixel(0, 0));
        session.Undo();
        session.ReplaceRegion(0, blue);

        Assert.IsFalse(session.CanRedo);
        Assert.HasCount(1, session.Operations);
        AeiEncodingResult validation = session.Validate();
        Assert.AreEqual(EditValidationState.Valid, session.ValidationState);
        Assert.AreEqual(new Rgba32(0, 0, 255, 255), validation.DecodedAtlas.GetPixel(0, 0));
    }

    [TestMethod]
    public async Task RecoveryRoundTripsAndRefusesChangedSourceHash()
    {
        string modRoot = Path.Combine(temporaryRoot, "mod");
        AeiFile file = ParseEditableAei();
        RgbaImage original = new AeiTextureDecoder().DecodeAtlas(file);
        AeiEditSession session = new(
            "textures/editable.aei",
            new string('b', 64),
            "Assets/Textures/textures/editable.aei",
            file,
            original);
        session.ReplaceRegion(0, SolidImage(1, 1, new Rgba32(12, 34, 56, 255)));
        RecoveryService recoveryService = new();

        await recoveryService.SaveAsync(modRoot, session);
        AeiRecoveryDocument recovery = (await recoveryService.LoadAsync(
            modRoot,
            session.SourceGameRelativePath))!;
        AeiEditSession restored = new(
            session.SourceGameRelativePath,
            session.OriginalSourceSha256,
            session.ModRelativeOutputPath,
            file,
            original);
        restored.Replay(recovery);

        Assert.AreEqual(new Rgba32(12, 34, 56, 255), restored.WorkingAtlas.GetPixel(0, 0));
        AeiEditSession conflict = new(
            session.SourceGameRelativePath,
            new string('c', 64),
            session.ModRelativeOutputPath,
            file,
            original);
        Assert.Throws<InvalidDataException>(() => conflict.Replay(recovery));
        Assert.AreEqual(EditValidationState.Conflict, conflict.ValidationState);
    }

    private IndexedAsset CreateIndexed(
        string relativePath,
        AssetKind kind,
        string version,
        AssetSupport support)
    {
        string path = Path.Combine(temporaryRoot, relativePath);
        return new IndexedAsset(
            path,
            relativePath,
            Path.GetFileName(relativePath),
            kind,
            AssetOwnership.Game,
            16,
            DateTimeOffset.UtcNow,
            version,
            version,
            support,
            support == AssetSupport.Supported,
            null);
    }

    private static byte[] CreateAeiHeader(byte format)
    {
        byte[] bytes = new byte[16];
        "AEimage\0"u8.CopyTo(bytes);
        bytes[8] = format;
        bytes[9] = 4;
        bytes[11] = 4;
        return bytes;
    }

    private static byte[] CreateAemHeader(string signature, byte flags)
    {
        byte[] signatureBytes = Encoding.ASCII.GetBytes(signature);
        byte[] bytes = new byte[Math.Max(16, signatureBytes.Length + 2)];
        signatureBytes.CopyTo(bytes, 0);
        bytes[signatureBytes.Length] = 0;
        bytes[signatureBytes.Length + 1] = flags;
        return bytes;
    }

    private static byte[] CreateRawAei(byte red, byte green, byte blue, byte alpha)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("AEimage\0"u8);
        writer.Write((byte)0x01);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write(red);
        writer.Write(green);
        writer.Write(blue);
        writer.Write(alpha);
        writer.Write((ushort)0);
        return stream.ToArray();
    }

    private static AeiFile ParseEditableAei()
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("AEimage\0"u8);
            writer.Write((byte)0x01);
            writer.Write((ushort)2);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write(new byte[] { 255, 0, 0, 255, 255, 255, 255, 255 });
            writer.Write((ushort)0);
        }

        stream.Position = 0;
        return new AeiParser().Parse(stream, "editable.aei");
    }

    private static RgbaImage SolidImage(int width, int height, Rgba32 color)
    {
        RgbaImage image = new(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image.SetPixel(x, y, color);
            }
        }

        return image;
    }

    private sealed class FakeDocument : IDocument
    {
        public FakeDocument(IndexedAsset asset)
        {
            Id = DocumentManager.NormalizeDocumentId(asset.FullPath);
            Title = asset.FileName;
            SourcePath = asset.FullPath;
        }

        public string Id { get; }

        public string Title { get; }

        public string Kind => "Fake";

        public string? SourcePath { get; }

        public bool IsReadOnly => true;

        public void Dispose()
        {
        }
    }

    private sealed class FakeProvider : IDocumentEditorProvider
    {
        public int OpenCount { get; private set; }

        public string Name => "Fake";

        public int Priority => 1;

        public bool CanOpen(IndexedAsset asset)
        {
            _ = asset;
            return true;
        }

        public Task<IDocument> OpenAsync(EditorOpenContext context)
        {
            OpenCount++;
            return Task.FromResult<IDocument>(new FakeDocument(context.Asset));
        }
    }

    private sealed class DelayedProvider : IDocumentEditorProvider
    {
        private int openCount;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int OpenCount => Volatile.Read(ref openCount);

        public string Name => "Delayed";

        public int Priority => 1;

        public bool CanOpen(IndexedAsset asset)
        {
            _ = asset;
            return true;
        }

        public async Task<IDocument> OpenAsync(EditorOpenContext context)
        {
            Interlocked.Increment(ref openCount);
            Started.TrySetResult();
            await Release.Task;
            return new FakeDocument(context.Asset);
        }
    }
}
