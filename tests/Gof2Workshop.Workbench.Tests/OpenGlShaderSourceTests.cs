using Avalonia.OpenGL;
using Gof2Workshop.App.Rendering;

namespace Gof2Workshop.Workbench.Tests;

[TestClass]
public sealed class OpenGlShaderSourceTests
{
    [TestMethod]
    public void MacStyleOpenGl32UsesGlsl150Core()
    {
        OpenGlShaderSources sources = OpenGlShaderSourceFactory.Create(
            new GlVersion(GlProfileType.OpenGL, 3, 2));

        Assert.StartsWith("#version 150 core", sources.Vertex, StringComparison.Ordinal);
        StringAssert.Contains(sources.Fragment, "out vec4 outColor");
    }

    [TestMethod]
    public void AngleOpenGlEs3UsesGlslEs300()
    {
        OpenGlShaderSources sources = OpenGlShaderSourceFactory.Create(
            new GlVersion(GlProfileType.OpenGLES, 3, 0));

        Assert.StartsWith("#version 300 es", sources.Vertex, StringComparison.Ordinal);
        StringAssert.Contains(sources.Fragment, "precision highp float");
    }

    [TestMethod]
    public void LegacyDesktopUsesAttributeShader()
    {
        OpenGlShaderSources sources = OpenGlShaderSourceFactory.Create(
            new GlVersion(GlProfileType.OpenGL, 2, 1));

        Assert.StartsWith("#version 120", sources.Vertex, StringComparison.Ordinal);
        StringAssert.Contains(sources.Vertex, "attribute vec3 aPosition");
    }
}
