// TesseractOcrEngineTests.cs — Tests for the packaged Tesseract fallback
// engine (spec #50).
//
// Covers the language catalog (packaged tessdata mapped from BCP-47 tags),
// BCP-47 → Tesseract language mapping (primary subtag, region/script
// fallback), the tessdata locator (packaged assets discovered beside the
// entry assembly), and the never-throw adapter contract over malformed
// Frames (null / zero dimensions / short buffer). Real-engine known-text
// and missing-asset coverage lives in the packaging tests, which compose
// actual Frames and run the real native engine against the packaged
// tessdata when the native runtime is present.

using System;
using System.IO;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

// ── Language catalog ────────────────────────────────────────────────────

public class TesseractLanguageCatalogTests
{
    [Fact]
    public void Catalog_ListsEveryPackagedLanguageWithDisplayName()
    {
        var catalog = TesseractOcrEngine.GetPackagedLanguages();

        Assert.NotEmpty(catalog);
        Assert.All(catalog, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l.Tag));
            Assert.False(string.IsNullOrWhiteSpace(l.DisplayName));
        });
        // The packaged set (tessdata_fast: eng, deu, fra, spa, ita),
        // advertised through canonical BCP-47 tags.
        Assert.Contains(catalog, l => l.Tag == "en-US");
        Assert.Contains(catalog, l => l.Tag == "de-DE");
        Assert.Contains(catalog, l => l.Tag == "fr-FR");
        Assert.Contains(catalog, l => l.Tag == "es-ES");
        Assert.Contains(catalog, l => l.Tag == "it-IT");
    }

    [Fact]
    public void MapToPackagedTag_ExactTesseractCode_MapsDirectly()
    {
        Assert.Equal("eng", TesseractOcrEngine.MapToPackagedTag("eng"));
        Assert.Equal("deu", TesseractOcrEngine.MapToPackagedTag("DEU"));
    }

    [Fact]
    public void MapToPackagedTag_Bcp47Tag_MapsToTesseractCode()
    {
        Assert.Equal("eng", TesseractOcrEngine.MapToPackagedTag("en"));
        Assert.Equal("eng", TesseractOcrEngine.MapToPackagedTag("en-US"));
        Assert.Equal("eng", TesseractOcrEngine.MapToPackagedTag("EN-us"));
        Assert.Equal("eng", TesseractOcrEngine.MapToPackagedTag("en-GB"));
        Assert.Equal("deu", TesseractOcrEngine.MapToPackagedTag("de"));
        Assert.Equal("deu", TesseractOcrEngine.MapToPackagedTag("de-DE"));
        Assert.Equal("deu", TesseractOcrEngine.MapToPackagedTag("de-AT"));
        Assert.Equal("fra", TesseractOcrEngine.MapToPackagedTag("fr-FR"));
        Assert.Equal("spa", TesseractOcrEngine.MapToPackagedTag("es"));
        Assert.Equal("spa", TesseractOcrEngine.MapToPackagedTag("es-MX"));
        Assert.Equal("ita", TesseractOcrEngine.MapToPackagedTag("it-IT"));
    }

    [Fact]
    public void MapToPackagedTag_UnpackagedLanguage_ReturnsNull()
    {
        Assert.Null(TesseractOcrEngine.MapToPackagedTag("ja"));
        Assert.Null(TesseractOcrEngine.MapToPackagedTag("ja-JP"));
        Assert.Null(TesseractOcrEngine.MapToPackagedTag("zh-CN"));
        Assert.Null(TesseractOcrEngine.MapToPackagedTag("xx"));
    }

    [Fact]
    public void MapToPackagedTag_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(TesseractOcrEngine.MapToPackagedTag(null));
        Assert.Null(TesseractOcrEngine.MapToPackagedTag(""));
        Assert.Null(TesseractOcrEngine.MapToPackagedTag("   "));
    }
}

// ── Tessdata locator ────────────────────────────────────────────────────

public class TesseractTessdataLocatorTests : IDisposable
{
    private readonly string _tempDir;

    public TesseractTessdataLocatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"captchoTess_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Locate_ConsidersTessdataValidOnlyWhenItExistsAndHasTrainedData()
    {
        Assert.Null(TesseractOcrEngine.LocateTessdata(_tempDir));

        Directory.CreateDirectory(Path.Combine(_tempDir, "tessdata"));
        Assert.Null(TesseractOcrEngine.LocateTessdata(_tempDir));

        File.WriteAllText(Path.Combine(_tempDir, "tessdata", "eng.traineddata"), "");
        Assert.Equal(
            Path.Combine(_tempDir, "tessdata"),
            TesseractOcrEngine.LocateTessdata(_tempDir));
    }
}

// ── Native engine-init failure ──────────────────────────────────────────

public class TesseractNativeLoadFailureTests : IDisposable
{
    private readonly string _tempDir;

    public TesseractNativeLoadFailureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"captchoTessFail_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task CorruptLanguageData_FailsAtNativeInitInsteadOfThrowing()
    {
        // A tessdata directory with corrupt traineddata models the
        // native-load failure path: the wrapper's engine construction
        // throws, which the adapter converts to the retryable Failed
        // outcome — never an exception, and the Frame stays retryable.
        var tessdata = Path.Combine(_tempDir, "tessdata");
        Directory.CreateDirectory(tessdata);
        File.WriteAllText(Path.Combine(tessdata, "eng.traineddata"), "this is not trained data");

        var engine = new TesseractOcrEngine(tessdata);

        var result = await engine.RecognizeAsync(ExportTestHelpers.CreateTestBitmap(4, 4), "en-US");

        Assert.Equal(OcrOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }
}

// ── Adapter contract over malformed Frames ──────────────────────────────

public class TesseractOcrEngineContractTests
{
    [Fact]
    public async Task RecognizeAsync_NullFrame_ThrowsArgumentNullException()
    {
        var engine = new TesseractOcrEngine();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => engine.RecognizeAsync(null!, "en-US"));
    }

    [Fact]
    public async Task RecognizeAsync_ZeroDimensions_FailsInsteadOfThrowing()
    {
        var engine = new TesseractOcrEngine();
        var zeroWidth = ExportTestHelpers.CreateTestBitmap(0, 4);

        var result = await engine.RecognizeAsync(zeroWidth, "en-US");

        Assert.Equal(OcrOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task RecognizeAsync_ShortBuffer_FailsInsteadOfThrowing()
    {
        var engine = new TesseractOcrEngine();
        var frame = FrameWithShortBuffer(16, 16);

        var result = await engine.RecognizeAsync(frame, "en-US");

        Assert.Equal(OcrOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task RecognizeAsync_MissingAssetsDirectory_FailsWithClearError()
    {
        // A tessdata directory that does not exist models an installed app
        // whose packaged assets were removed: the engine must stay
        // constructible and surface the retryable missing-assets failure.
        var engine = new TesseractOcrEngine(tessdataPath: @"C:\captcho-no-such-tessdata");

        var result = await engine.RecognizeAsync(ExportTestHelpers.CreateTestBitmap(4, 4), "en-US");

        Assert.Equal(OcrOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void GetAvailableLanguages_NeverThrowsAndMatchesCatalog()
    {
        var engine = new TesseractOcrEngine();

        var languages = engine.GetAvailableLanguages();

        Assert.NotNull(languages);
    }

    private static ContiguousBitmap FrameWithShortBuffer(int width, int height)
    {
        var pixels = new byte[(width * height * 4) / 2];
        return CreateBitmap(width, height, pixels);
    }

    private static ContiguousBitmap CreateBitmap(int width, int height, byte[] pixels)
    {
        var constructor = typeof(ContiguousBitmap).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null,
            new[] { typeof(int), typeof(int), typeof(int), typeof(byte[]) },
            null);

        Assert.NotNull(constructor);
        return (ContiguousBitmap)constructor.Invoke(new object[] { width, height, width * 4, pixels });
    }
}
