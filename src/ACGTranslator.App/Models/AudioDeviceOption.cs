namespace ACGTranslator.Models;

public sealed class AudioDeviceOption
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public override string ToString() => Name;
}
