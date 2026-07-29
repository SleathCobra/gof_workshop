using System.Buffers.Binary;
using Gof2Workshop.Binary;
using Gof2Workshop.Core;
using Gof2Workshop.Export;

namespace Gof2Workshop.Formats.Aei.Tests;

[TestClass]
public sealed class AeiParserTests
{
    private static readonly int[] ExpectedMipWidths = [4, 2, 1];
    private static readonly int[] ExpectedMipOffsets = [0, 8, 16];

    [TestMethod]
    public void RawRgbaFixtureParsesRegionsSymbolsAndPixels()
    {
        byte[] fixture = CreateRawFixture(includeSymbol: true);
        using MemoryStream stream = new(fixture);

        AeiFile file = new AeiParser().Parse(stream, "synthetic.aei");
        RgbaImage image = new AeiTextureDecoder().DecodeAtlas(file);

        Assert.AreEqual(AeiCompressionFormat.UncompressedUi, file.Format.Format);
        Assert.AreEqual(2, file.Width);
        Assert.AreEqual(1, file.Height);
        Assert.HasCount(1, file.Regions);
        Assert.HasCount(1, file.SymbolMaps);
        Assert.AreEqual('A', file.SymbolMaps[0].Symbols[0].Character);
        Assert.AreEqual(new Rgba32(255, 0, 0, 255), image.GetPixel(0, 0));
        Assert.AreEqual(new Rgba32(0, 255, 0, 128), image.GetPixel(1, 0));
    }

    [TestMethod]
    public void Dxt1KnownBlockDecodesRed()
    {
        byte[] block = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(block, 0xF800);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), 0x07E0);
        byte[] fixture = CreateCompressedFixture(0x20, 4, 4, block);
        using MemoryStream stream = new(fixture);

        AeiFile file = new AeiParser().Parse(stream, "bc1.aei");
        RgbaImage image = new AeiTextureDecoder().DecodeAtlas(file);

        Assert.AreEqual(new Rgba32(255, 0, 0, 255), image.GetPixel(0, 0));
        Assert.AreEqual(new Rgba32(255, 0, 0, 255), image.GetPixel(3, 3));
    }

    [TestMethod]
    public void Dxt5KnownBlockDecodesOpaqueBlue()
    {
        byte[] block = new byte[16];
        block[0] = 255;
        block[1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 0x001F);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(10), 0x0000);
        byte[] fixture = CreateCompressedFixture(0x24, 4, 4, block);
        using MemoryStream stream = new(fixture);

        AeiFile file = new AeiParser().Parse(stream, "bc3.aei");
        RgbaImage image = new AeiTextureDecoder().DecodeAtlas(file);

        Assert.AreEqual(new Rgba32(0, 0, 255, 255), image.GetPixel(2, 2));
    }

    [TestMethod]
    [DataRow((byte)0x0D, (ushort)16, (ushort)8, 32)]
    [DataRow((byte)0x10, (ushort)8, (ushort)8, 32)]
    [DataRow((byte)0x11, (ushort)4, (ushort)4, 16)]
    [DataRow((byte)0x40, (ushort)4, (ushort)4, 8)]
    [DataRow((byte)0x17, (ushort)4, (ushort)4, 16)]
    public void MobileCodecKnownZeroBlockDecodesSafely(
        byte format,
        ushort width,
        ushort height,
        int payloadLength)
    {
        using MemoryStream stream = new(
            CreateCompressedFixture(format, width, height, new byte[payloadLength]));
        AeiFile file = new AeiParser().Parse(
            stream,
            "mobile.aei",
            new AeiParserOptions(ProfileCatalog.Android));

        RgbaImage image = new AeiTextureDecoder().DecodeAtlas(file);

        Assert.AreEqual(width, image.Width);
        Assert.AreEqual(height, image.Height);
        Assert.AreEqual(checked(width * height * 4), image.PixelBytes.Length);
    }

    [TestMethod]
    public void MipmappedDxt1BuildsCompleteSurfaceChain()
    {
        byte[] payload = new byte[24];
        byte[] fixture = CreateCompressedFixture(0x22, 4, 4, payload);
        using MemoryStream stream = new(fixture);

        AeiFile file = new AeiParser().Parse(stream, "mips.aei");

        Assert.AreEqual(3, file.MipLevelCount);
        CollectionAssert.AreEqual(ExpectedMipWidths, file.Surfaces.Select(surface => surface.Width).ToArray());
        CollectionAssert.AreEqual(ExpectedMipOffsets, file.Surfaces.Select(surface => surface.PayloadOffset).ToArray());
    }

    [TestMethod]
    public void TruncatedPayloadFailsAsCorruptWithFieldAndOffset()
    {
        byte[] fixture = CreateCompressedFixture(0x20, 4, 4, new byte[8]);
        Array.Resize(ref fixture, fixture.Length - 4);
        using MemoryStream stream = new(fixture);

        FormatParseException exception = Assert.Throws<FormatParseException>(
            () => new AeiParser().Parse(stream, "truncated.aei"));

        Assert.AreEqual(FormatFailureKind.Corrupt, exception.FailureKind);
        Assert.AreEqual("payload", exception.Field);
        Assert.IsGreaterThanOrEqualTo(0, exception.Offset);
    }

    [TestMethod]
    public void UnknownSignatureFailsAsUnsupported()
    {
        byte[] fixture = CreateRawFixture(includeSymbol: false);
        fixture[0] = (byte)'X';
        using MemoryStream stream = new(fixture);

        FormatParseException exception = Assert.Throws<FormatParseException>(
            () => new AeiParser().Parse(stream, "unknown.aei"));

        Assert.AreEqual(FormatFailureKind.Unsupported, exception.FailureKind);
        Assert.AreEqual("magic", exception.Field);
    }

    [TestMethod]
    public void ResearchModeRecordsFieldOffsets()
    {
        using MemoryStream stream = new(CreateRawFixture(includeSymbol: false));
        AeiFile file = new AeiParser().Parse(
            stream,
            "trace.aei",
            AeiParserOptions.Pc1X with { ResearchDiagnostics = true });

        Assert.IsNotNull(file.Trace);
        Assert.IsTrue(file.Trace.Entries.Any(entry => entry.Field == "width" && entry.Offset == 9));
        Assert.IsTrue(file.Trace.Entries.Any(entry => entry.Section == "regions"));
    }

    [TestMethod]
    public void PngWriterProducesPngSignature()
    {
        RgbaImage image = new(1, 1);
        image.SetPixel(0, 0, new Rgba32(12, 34, 56, 255));
        using MemoryStream output = new();

        PngWriter.Write(image, output);

        CollectionAssert.AreEqual(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
            output.ToArray()[..8]);
    }

    [TestMethod]
    public void FixedPointConversionHandlesPositiveAndNegativeValues()
    {
        Assert.AreEqual(1.23046875, BoundedBinaryReader.FixedPointToDouble(315, 8), 1e-12);
        Assert.AreEqual(-0.5, BoundedBinaryReader.FixedPointToDouble(-2048, 12), 1e-12);
    }

    [TestMethod]
    public void WriterRoundTripsContainerAndSupportsRawPayloadReplacement()
    {
        byte[] fixture = CreateRawFixture(includeSymbol: true);
        using MemoryStream input = new(fixture);
        AeiFile file = new AeiParser().Parse(input, "write.aei");
        using MemoryStream output = new();

        new AeiWriter().Write(file, output);

        CollectionAssert.AreEqual(fixture, output.ToArray());
        byte[] replacement = [0, 0, 255, 255, 255, 255, 0, 255];
        using MemoryStream replaced = new();
        new AeiWriter().Write(file, replaced, replacement);
        replaced.Position = 0;
        RgbaImage decoded = new AeiTextureDecoder().DecodeAtlas(
            new AeiParser().Parse(replaced, "replaced.aei"));
        Assert.AreEqual(new Rgba32(0, 0, 255, 255), decoded.GetPixel(0, 0));
    }

    [TestMethod]
    public void RawEncoderReconstructsReparsesAndDecodesWorkingAtlas()
    {
        using MemoryStream source = new(CreateRawFixture(includeSymbol: false));
        AeiFile file = new AeiParser().Parse(source, "raw-encode.aei");
        RgbaImage working = new(2, 1);
        working.SetPixel(0, 0, new Rgba32(20, 40, 60, 80));
        working.SetPixel(1, 0, new Rgba32(100, 120, 140, 160));

        AeiEncodingResult result = new AeiReconstructionService().ReconstructAndValidate(
            file,
            working);

        Assert.AreEqual(AeiCompressionFormat.UncompressedUi, result.ReparsedFile.Format.Format);
        Assert.AreEqual(0, result.AbsolutePixelError);
        Assert.AreEqual((byte)0, result.MaximumChannelError);
        Assert.AreEqual(new Rgba32(20, 40, 60, 80), result.DecodedAtlas.GetPixel(0, 0));
        Assert.AreEqual(new Rgba32(100, 120, 140, 160), result.DecodedAtlas.GetPixel(1, 0));
    }

    [TestMethod]
    [DataRow((byte)0x20)]
    [DataRow((byte)0x21)]
    [DataRow((byte)0x24)]
    public void BcEncoderReconstructsAndReparsesPreservedContainer(byte formatId)
    {
        int payloadLength = formatId == 0x20 ? 8 : 16;
        using MemoryStream source = new(
            CreateCompressedFixture(formatId, 4, 4, new byte[payloadLength]));
        AeiFile file = new AeiParser().Parse(source, "encode.aei");
        RgbaImage working = new(4, 4);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                working.SetPixel(x, y, new Rgba32(220, 40, 20, (byte)(64 + (x * 40))));
            }
        }

        AeiEncodingResult result = new AeiReconstructionService().ReconstructAndValidate(
            file,
            working,
            new AeiEncodingOptions(AeiEncodingQuality.Fast));

        Assert.AreEqual(file.Format.Format, result.ReparsedFile.Format.Format);
        Assert.AreEqual(file.Payload.Length, result.Payload.Length);
        Assert.AreEqual(4, result.DecodedAtlas.Width);
        Assert.AreEqual(4, result.DecodedAtlas.Height);
        Assert.IsTrue(result.DecodedAtlas.GetPixel(0, 0).R > 150);
        if (formatId != 0x20)
        {
            Assert.IsTrue(result.DecodedAtlas.GetPixel(0, 0).A < 150);
        }
    }

    [TestMethod]
    public void RegionReplacementIsImmutableAndReportsOverlapAndDifference()
    {
        RgbaImage original = new(4, 4);
        AeiRegion region = new(0, 1, 1, 2, 2, 0);
        RgbaImage replacement = new(2, 2);
        replacement.PixelBytes.Fill(255);

        RgbaImage working = AeiAtlasEditing.ReplaceRegion(original, region, replacement);
        AeiPixelDifference difference = AeiAtlasEditing.Compare(original, working);
        IReadOnlyList<AeiRegionOverlap> overlaps = AeiAtlasEditing.FindOverlaps(
        [
            region,
            new AeiRegion(1, 2, 2, 2, 2, 0),
            new AeiRegion(2, 3, 0, 1, 1, 0),
        ]);

        Assert.AreEqual(Rgba32.Transparent, original.GetPixel(1, 1));
        Assert.AreEqual(new Rgba32(255, 255, 255, 255), working.GetPixel(1, 1));
        Assert.AreEqual(4, difference.ChangedPixels);
        Assert.AreEqual(4, difference.ChangedAlphaPixels);
        Assert.HasCount(1, overlaps);
    }

    [TestMethod]
    public void RegionReplacementRejectsDimensionMismatch()
    {
        RgbaImage original = new(4, 4);
        AeiRegion region = new(0, 1, 1, 2, 2, 0);
        Assert.Throws<InvalidDataException>(
            () => AeiAtlasEditing.ReplaceRegion(original, region, new RgbaImage(1, 2)));
    }

    private static byte[] CreateRawFixture(bool includeSymbol)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("AEimage\0"u8);
        writer.Write((byte)0x01);
        writer.Write((ushort)2);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)2);
        writer.Write((ushort)1);
        writer.Write(new byte[] { 255, 0, 0, 255, 0, 255, 0, 128 });
        writer.Write((ushort)(includeSymbol ? 1 : 0));
        if (includeSymbol)
        {
            writer.Write((ushort)1);
            writer.Write((ushort)'A');
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
        }

        return stream.ToArray();
    }

    private static byte[] CreateCompressedFixture(
        byte format,
        ushort width,
        ushort height,
        byte[] payload)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("AEimage\0"u8);
        writer.Write(format);
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(width);
        writer.Write(height);
        writer.Write((uint)payload.Length);
        writer.Write(payload);
        writer.Write((ushort)0);
        return stream.ToArray();
    }
}
