using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Promin.Agent.Tests;

public sealed class AgentWindowModelTests
{
    // The wording tests state the English texts, whatever language Windows is in.
    [ModuleInitializer]
    internal static void WordingTestsReadEnglish() =>
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

    private static AgentWindowModel Refreshed(AgentPaths paths)
    {
        var model = new AgentWindowModel(paths);
        model.Refresh();
        return model;
    }

    private static void WritePins(AgentPaths paths, string json)
    {
        Directory.CreateDirectory(paths.AgentDirectory);
        File.WriteAllText(paths.MastersFile, json);
    }

    [Fact]
    public void NoFiles_SaysThereIsNoCode()
    {
        using var temp = new TempDirectory();
        var model = Refreshed(temp.Paths);

        Assert.Equal(StripSeverity.Informational, model.Severity);
        Assert.Equal(Strings.Status_NoCode, model.Message);
        Assert.Null(model.StoredCode);
    }

    [Fact]
    public void ACode_NoPins_SaysNotPairedYet()
    {
        using var temp = new TempDirectory();
        new AgentSettings(temp.Paths.RoomCodeFile).Save("482913");
        var model = Refreshed(temp.Paths);

        Assert.Equal(StripSeverity.Informational, model.Severity);
        Assert.Equal(Strings.Status_NotPaired, model.Message);
        Assert.Equal("482913", model.StoredCode);
    }

    [Fact]
    public void APin_SaysPaired()
    {
        using var temp = new TempDirectory();
        new AgentSettings(temp.Paths.RoomCodeFile).Save("482913");
        WritePins(temp.Paths, """{"console-1":"AB12"}""");
        var model = Refreshed(temp.Paths);

        Assert.Equal(StripSeverity.Success, model.Severity);
        Assert.Equal(Strings.Status_Paired, model.Message);
    }

    [Fact]
    public void APin_WithoutACode_StillSaysPaired()
    {
        using var temp = new TempDirectory();
        WritePins(temp.Paths, """{"console-1":"AB12"}""");
        var model = Refreshed(temp.Paths);

        Assert.Equal(StripSeverity.Success, model.Severity);
        Assert.Null(model.StoredCode);
    }

    [Fact]
    public void UnreadablePins_IsAnError_NotNotPaired()
    {
        using var temp = new TempDirectory();
        WritePins(temp.Paths, "not json");
        var model = Refreshed(temp.Paths);

        Assert.Equal(StripSeverity.Error, model.Severity);
        Assert.StartsWith("Cannot tell whether this PC is paired", model.Message);
    }

    [Fact]
    public void UnusableCodeFile_IsAnError()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.Paths.RoomCodeFile)!);
        File.WriteAllText(temp.Paths.RoomCodeFile, "{}");
        var model = Refreshed(temp.Paths);

        Assert.Equal(StripSeverity.Error, model.Severity);
        Assert.Equal(Strings.Status_CodeUnusable, model.Message);
    }

    [Fact]
    public void Save_RefusesFourDigits_AndWritesNothing()
    {
        using var temp = new TempDirectory();
        var model = Refreshed(temp.Paths);

        Assert.False(model.Save("4829"));
        Assert.Equal(StripSeverity.Error, model.Severity);
        Assert.Equal(Strings.Status_BadCode, model.Message);
        Assert.False(File.Exists(temp.Paths.RoomCodeFile));
    }

    [Fact]
    public void Save_StoresTheNormalisedCode()
    {
        using var temp = new TempDirectory();
        var model = Refreshed(temp.Paths);

        Assert.True(model.Save("482-913"));
        Assert.Equal("482913", new AgentSettings(temp.Paths.RoomCodeFile).Load());
        Assert.Equal("482913", model.StoredCode);
    }

    [Fact]
    public void Save_ThatCannotWrite_IsAnError()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Paths.RoomCodeFile);
        var model = Refreshed(temp.Paths);

        Assert.False(model.Save("482913"));
        Assert.Equal(StripSeverity.Error, model.Severity);
        Assert.StartsWith("The code could not be saved: ", model.Message);
    }

    [Fact]
    public void Refresh_KeepsASaveErrorShowing()
    {
        using var temp = new TempDirectory();
        var model = Refreshed(temp.Paths);

        model.Save("4829");
        model.Refresh();
        Assert.Equal(StripSeverity.Error, model.Severity);
        Assert.Equal(Strings.Status_BadCode, model.Message);

        model.Edited();
        Assert.Equal(Strings.Status_NoCode, model.Message);
    }

    [Fact]
    public void EveryText_HasAUkrainianOne()
    {
        var english = Texts(CultureInfo.InvariantCulture);
        var ukrainian = Texts(CultureInfo.GetCultureInfo("uk"));

        Assert.NotEmpty(english);
        foreach (var (key, text) in english)
        {
            Assert.True(ukrainian.TryGetValue(key, out var translated), $"{key} has no Ukrainian text.");
            Assert.Equal(text.Contains("{0}"), translated!.Contains("{0}"));
        }

        Assert.Empty(ukrainian.Keys.Except(english.Keys));
    }

    private static Dictionary<string, string> Texts(CultureInfo culture) =>
        Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);
}
