using ACGTranslator.Models;
using ACGTranslator.Services;

namespace ACGTranslator.Tests;

public class CalibrationSettingsTests
{
    [Fact]
    public void Applying_tuned_values_preserves_language_translation_history_and_segmentation()
    {
        var settings = new AppSettings { Language = "auto", TranslatorId = "argos", TurnEnhancement = true, MaxSegmentMs = 9000, SaveHistory = false, OutputDeviceId = "chosen-device" };
        new AsrCalibrationValues("", "local-model", "cpu", 1).CopyTo(settings);
        Assert.Equal(1, settings.AsrBeamSize); Assert.Equal("local-model", settings.AsrModelPath);
        Assert.Equal("auto", settings.Language); Assert.Equal("argos", settings.TranslatorId);
        Assert.Equal(9000, settings.MaxSegmentMs); Assert.True(settings.TurnEnhancement); Assert.False(settings.SaveHistory);
        Assert.Equal("chosen-device", settings.OutputDeviceId);
    }
    [Fact]
    public void Backup_can_restore_automatic_model_selection_without_losing_other_changes()
    {
        var settings = new AppSettings { AsrModelId = "whisper-base", AsrDevice = "auto", AsrBeamSize = 0 };
        var backup = AsrCalibrationValues.From(settings);
        new AsrCalibrationValues("", "local", "cpu", 2).CopyTo(settings); settings.FontSize = 40;
        backup.CopyTo(settings);
        Assert.Equal("whisper-base", settings.AsrModelId); Assert.Equal("", settings.AsrModelPath);
        Assert.Equal("auto", settings.AsrDevice); Assert.Equal(0, settings.AsrBeamSize); Assert.Equal(40, settings.FontSize);
    }
    [Fact]
    public void Invalid_patch_is_rejected_before_any_fields_change()
    {
        var settings = new AppSettings(); string before = AsrCalibrationValues.Context(settings);
        Assert.Throws<InvalidDataException>(() => new AsrCalibrationValues("", "bad", "cpu", -1).CopyTo(settings));
        Assert.Equal(before, AsrCalibrationValues.Context(settings));
    }
}
