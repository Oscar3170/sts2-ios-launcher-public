using System.Text.Json;

namespace STS2Mobile.Platform.Tests;

public sealed class PhoneGraphicsSettingsTests : IDisposable
{
    private static readonly PhoneGraphicsSettings Defaults = PhoneGraphicsSettings.Default;
    private readonly string _directory = Directory.CreateTempSubdirectory("sts2-phone-graphics-").FullName;
    private string SettingsFile => Path.Combine(_directory, PhoneGraphicsSettings.FileName);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly PhoneGraphicsSettings Off = Defaults.WithPreset(BatteryPreset.Off);

    [Fact]
    public void DefaultsAreTheMaximumPreset()
    {
        Assert.True(Defaults is
        {
            Resolution: RenderResolution.Low,
            LowerIdleFrameRate: true,
            CoolDownWhenHot: true,
            MsaaCleared: false,
            PortraitMode: false,
            PortraitDefault: false,
            FlatHand: false,
            ReduceEffects: true,
            StartInPortrait: false,
            Preset: BatteryPreset.Maximum,
        });
        Assert.Equal(2.0 / 3.0, Defaults.ResolutionFactor, 1e-9);
    }

    [Fact]
    public void OffKeepsTheGamesBehaviour()
    {
        Assert.Equal(BatteryPreset.Off, Off.Preset);
        Assert.Equal(1, Off.ResolutionFactor);
        Assert.False(Off.ReduceEffects);
    }

    [Fact]
    public void BalancedPreset()
    {
        var balanced = Off.WithPreset(BatteryPreset.Balanced);
        Assert.True(balanced is { Resolution: RenderResolution.Reduced, LowerIdleFrameRate: false, CoolDownWhenHot: true, ReduceEffects: true, Preset: BatteryPreset.Balanced });
    }

    [Fact]
    public void MaximumPreset()
    {
        var maximum = Off.WithPreset(BatteryPreset.Maximum);
        Assert.True(maximum is { Resolution: RenderResolution.Low, LowerIdleFrameRate: true, CoolDownWhenHot: true, ReduceEffects: true, Preset: BatteryPreset.Maximum });
    }

    [Fact]
    public void ReduceEffectsRoundTripsAndIsPartOfThePresets()
    {
        Assert.True(Defaults.ReduceEffects);
        Assert.True(Defaults.WithReduceEffects(false) is { ReduceEffects: false, Preset: BatteryPreset.Custom });
        Assert.True(Off.WithReduceEffects(true) is { ReduceEffects: true, Preset: BatteryPreset.Custom });
        Assert.True(Defaults.WithPreset(BatteryPreset.Balanced) is { ReduceEffects: true, Preset: BatteryPreset.Balanced });

        var reduced = Off.WithReduceEffects(true);
        reduced.Save(SettingsFile);
        Assert.Equal(reduced, PhoneGraphicsSettings.Load(SettingsFile));

        var full = Defaults.WithReduceEffects(false);
        full.Save(SettingsFile);
        Assert.Equal(full, PhoneGraphicsSettings.Load(SettingsFile));
    }

    [Fact]
    public void OlderFilesTakeTheEffectsTheirPresetMeans()
    {
        // A file written before the setting existed keeps its preset's own answer: an existing Maximum (or Balanced) file
        // keeps its preset and gains the optimisation, while Off and a hand-tuned file keep the game's own effects.
        File.WriteAllText(SettingsFile, "{\"resolution\":\"low\",\"lowerIdleFrameRate\":true,\"coolDownWhenHot\":true}");
        Assert.True(PhoneGraphicsSettings.Load(SettingsFile) is { ReduceEffects: true, Preset: BatteryPreset.Maximum });

        File.WriteAllText(SettingsFile, "{\"resolution\":\"reduced\",\"coolDownWhenHot\":true}");
        Assert.True(PhoneGraphicsSettings.Load(SettingsFile) is { ReduceEffects: true, Preset: BatteryPreset.Balanced });

        File.WriteAllText(SettingsFile, "{\"resolution\":\"full\"}");
        Assert.True(PhoneGraphicsSettings.Load(SettingsFile) is { ReduceEffects: false, Preset: BatteryPreset.Off });

        File.WriteAllText(SettingsFile, "{\"resolution\":\"low\",\"coolDownWhenHot\":true}");
        Assert.True(PhoneGraphicsSettings.Load(SettingsFile) is { ReduceEffects: false, Preset: BatteryPreset.Custom });
    }

    [Fact]
    public void AFileThatHasTheSettingKeepsItsChoice()
    {
        // Only a missing key is inferred; an explicit off stays off, as Custom on the Maximum-shaped choices.
        File.WriteAllText(SettingsFile,
            "{\"resolution\":\"low\",\"lowerIdleFrameRate\":true,\"coolDownWhenHot\":true,\"reduceEffects\":false}");
        Assert.True(PhoneGraphicsSettings.Load(SettingsFile) is { ReduceEffects: false, Preset: BatteryPreset.Custom });
    }

    [Fact]
    public void OtherCombinationsAreCustom()
    {
        Assert.Equal(BatteryPreset.Custom, (Off with { LowerIdleFrameRate = true }).Preset);
        Assert.Equal(Defaults, Defaults.WithPreset(BatteryPreset.Custom));
    }

    [Fact]
    public void MissingFileGivesTheDefaults() => Assert.Equal(Defaults, PhoneGraphicsSettings.Load(SettingsFile));

    [Fact]
    public void SettingsSurviveSaveAndLoad()
    {
        var chosen = Defaults.WithPreset(BatteryPreset.Off) with { MsaaCleared = true };
        chosen.Save(SettingsFile);
        Assert.Equal(chosen, PhoneGraphicsSettings.Load(SettingsFile));
        Assert.False(File.Exists(SettingsFile + ".partial"));
    }

    [Fact]
    public void NativeReaderGetsAPlainRenderScale()
    {
        Defaults.WithPreset(BatteryPreset.Maximum).Save(SettingsFile);
        var saved = JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement;
        Assert.Equal(0.6667, saved.GetProperty("renderScale").GetDouble(), 1e-9);
    }

    [Fact]
    public void UnreadableFileGivesTheDefaults()
    {
        File.WriteAllText(SettingsFile, "{ not json");
        Assert.Equal(Defaults, PhoneGraphicsSettings.Load(SettingsFile));
    }

    [Fact]
    public void WronglyTypedValuesFallBackOneByOne()
    {
        File.WriteAllText(SettingsFile, "{\"resolution\":\"reduced\",\"coolDownWhenHot\":\"yes\"}");
        Assert.True(PhoneGraphicsSettings.Load(SettingsFile) is { Resolution: RenderResolution.Reduced, CoolDownWhenHot: false, PortraitMode: false, PortraitDefault: false });
    }

    [Fact]
    public void PortraitSettingsRoundTripAndKeepBatteryPresetsIndependent()
    {
        var portraitOnly = Defaults.WithPortraitMode(true);
        Assert.True(portraitOnly is { PortraitMode: true, PortraitDefault: false, StartInPortrait: false, Preset: BatteryPreset.Maximum });
        portraitOnly.Save(SettingsFile);
        Assert.Equal(portraitOnly, PhoneGraphicsSettings.Load(SettingsFile));

        var portraitAtLaunch = portraitOnly.WithPortraitDefault(true);
        Assert.True(portraitAtLaunch is { PortraitMode: true, PortraitDefault: true, StartInPortrait: true, Preset: BatteryPreset.Maximum });
        Assert.True(portraitAtLaunch.WithPreset(BatteryPreset.Balanced) is { PortraitMode: true, PortraitDefault: true, Preset: BatteryPreset.Balanced });
        portraitAtLaunch.Save(SettingsFile);
        Assert.Equal(portraitAtLaunch, PhoneGraphicsSettings.Load(SettingsFile));

        var backToLandscape = portraitAtLaunch.WithPortraitMode(false);
        Assert.True(backToLandscape is { PortraitMode: false, PortraitDefault: false, StartInPortrait: false });
    }

    [Fact]
    public void FlatHandRoundTripsAndDefaultsOffForOlderFiles()
    {
        Assert.False(Defaults.FlatHand);
        var flat = Defaults.WithFlatHand(true);
        flat.Save(SettingsFile);
        Assert.True(PhoneGraphicsSettings.Load(SettingsFile) is { FlatHand: true });
        Assert.True(flat.WithPreset(BatteryPreset.Balanced) is { FlatHand: true, Preset: BatteryPreset.Balanced });

        File.WriteAllText(SettingsFile, "{\"resolution\":\"low\",\"portraitMode\":true,\"portraitDefault\":true}");
        Assert.True(PhoneGraphicsSettings.Load(SettingsFile) is { FlatHand: false });
    }

    [Fact]
    public void PerformanceLogRoundTripsAndDefaultsOffForOlderFiles()
    {
        Assert.False(Defaults.PerformanceLog);
        var logging = Defaults.WithPerformanceLog(true);
        logging.Save(SettingsFile);
        Assert.True(PhoneGraphicsSettings.Load(SettingsFile) is { PerformanceLog: true });
        Assert.True(logging.WithPreset(BatteryPreset.Balanced) is { PerformanceLog: true, Preset: BatteryPreset.Balanced });

        File.WriteAllText(SettingsFile, "{\"resolution\":\"low\",\"portraitMode\":true,\"portraitDefault\":true}");
        Assert.True(PhoneGraphicsSettings.Load(SettingsFile) is { PerformanceLog: false });
    }

    [Fact]
    public void PortraitDefaultIsClearedOnLoadWhenPortraitModeIsOff()
    {
        File.WriteAllText(SettingsFile, "{\"resolution\":\"low\",\"portraitMode\":false,\"portraitDefault\":true}");
        Assert.True(PhoneGraphicsSettings.Load(SettingsFile) is { PortraitMode: false, PortraitDefault: false, StartInPortrait: false });
    }
}
