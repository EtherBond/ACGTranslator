using System.Collections.Concurrent;
using System.Text.Json;
using ACGTranslator.Models;
using ACGTranslator.Services;

namespace ACGTranslator.Tests;
public class TurnTests
{
    private sealed class Recognizer(AsrResult result) : ISpeechRecognizer
    {
        public Task<AsrResult> RecognizeAsync(float[] s, CancellationToken c = default) => Task.FromResult(result);
    }
    private sealed class Translator : ITextTranslator
    {
        public Task<string> TranslateAsync(string text, string language, CancellationToken c = default) => Task.FromResult("译" + text);
    }

    [Fact]
    public async Task Two_turns_are_translated_exported_in_order_without_stretching_short_cues()
    {
        var result = new AsrResult { Text="merged", Segments = [
            new() {Text="first",Language="en",StartSeconds=.1,EndSeconds=.3},
            new() {Text="second",Language="en",StartSeconds=.31,EndSeconds=1.3}] };
        var processor=new SubtitleProcessor(new Recognizer(result),new Translator());
        var entries=new ConcurrentQueue<SubtitleEntry>();
        processor.Completed += (_,e)=>entries.Enqueue(e.Entry);
        processor.Enqueue(new(new float[32000],TimeSpan.FromSeconds(10),TimeSpan.FromSeconds(12)),
            new(Guid.NewGuid(),"test"));
        await processor.DisposeAsync();
        Assert.Equal(["译first","译second"],entries.Select(e=>e.ChineseText));
        var cues=entries.ToArray();
        Assert.Equal(TimeSpan.FromSeconds(10.3),cues[0].End);
        Assert.True(cues[0].End<cues[1].Start);
        Assert.NotNull(cues[0].TurnGroupId);
        Assert.Equal(cues[0].TurnGroupId,cues[1].TurnGroupId);
        var srt=SubtitleExporter.ToSrt(entries,true);
        Assert.Contains("00:00:10,100 --> 00:00:10,300",srt);
        Assert.Contains("译second",srt);
    }

    [Fact]
    public async Task Warning_does_not_drop_fallback_subtitle()
    {
        var p=new SubtitleProcessor(new Recognizer(new() {Text="keep",Language="en",EndSeconds=1,Warning="fallback"}),new Translator());
        string? warning=null, text=null;
        p.Failed+=(_,e)=>warning=e.Message;
        p.Completed+=(_,e)=>text=e.Entry.ChineseText;
        p.Enqueue(new(new float[16000],TimeSpan.Zero,TimeSpan.FromSeconds(1)),new(Guid.NewGuid(),"test"));
        await p.DisposeAsync();
        Assert.Equal("fallback",warning);
        Assert.Equal("译keep",text);
    }

    [Theory]
    [InlineData("{\"text\":\"legacy\",\"language\":\"en\"}",0)]
    [InlineData("{\"text\":\"merged\",\"segments\":[{\"text\":\"a\",\"start\":0,\"end\":1},{\"text\":\"b\",\"start\":1,\"end\":2}]}",2)]
    public void Worker_protocol_handles_legacy_and_multiple_results(string json,int count)
    {
        using var document=JsonDocument.Parse(json);
        var result=LocalAsrClient.ParseResult(document.RootElement,2);
        Assert.Equal(count,result.Segments.Count);
    }

    [Fact]
    public void Overlay_keeps_two_turns_and_ignores_old_completions()
    {
        var display=new SubtitleDisplay();
        var id=Guid.NewGuid();
        var first=new SubtitleEntry {TurnGroupId=id,SourceText="first"};
        var second=new SubtitleEntry {TurnGroupId=id,SourceText="second"};
        display.Recognized(first); display.Recognized(second);
        Assert.Equal(2,display.Entries.Count);
        Assert.True(display.Completed(first));
        var next=new SubtitleEntry {SourceText="next"};
        display.Recognized(next);
        Assert.Equal(2,display.Entries.Count);
        Assert.True(display.Completed(next));
        Assert.False(display.Completed(second));
        Assert.Equal(next.Id,Assert.Single(display.Entries).Id);
    }

    [Fact]
    public void Recognition_running_ahead_does_not_hide_completed_translations()
    {
        var display = new SubtitleDisplay();
        var first = new SubtitleEntry { SourceText = "one" };
        var second = new SubtitleEntry { SourceText = "two" };
        var third = new SubtitleEntry { SourceText = "three" };
        display.Recognized(first);
        display.Recognized(second);
        display.Recognized(third);
        first.ChineseText = "一";
        Assert.True(display.Completed(first));
        Assert.Equal("一", Assert.Single(display.Entries).ChineseText);
        second.ChineseText = "二";
        Assert.True(display.Completed(second));
        Assert.Equal("二", Assert.Single(display.Entries).ChineseText);
        display.Clear();
        Assert.False(display.Completed(third));
        Assert.Empty(display.Entries);
    }

    [Fact]
    public void Old_settings_keep_defaults_and_turn_model_has_own_directory()
    {
        var settings=JsonSerializer.Deserialize<AppSettings>("{\"ShowSource\":false}")!;
        Assert.True(settings.TurnEnhancement);
        var store=new ModelStore(new(),Path.GetTempPath());
        var path=store.GetDirectory(new() {Kind=CatalogKind.Speaker,Id="test"});
        Assert.Equal(Path.Combine(Path.GetTempPath(),"speakers","test"),path);
    }
}
