using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ACGTranslator.Services;

/// <summary>CPU-only adapter for faster-whisper 1.2.1's Silero v6 ONNX asset.</summary>
public sealed class SileroSpeechGate : IDisposable
{
    private readonly InferenceSession _session;
    private float[] _h = new float[128], _c = new float[128];
    private readonly float[] _input = new float[576];
    private int _frames;
    public SileroSpeechGate(string path)
    {
        using var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
        _session = new InferenceSession(path, options);
        if (!_session.InputMetadata.ContainsKey("h") || !_session.InputMetadata.ContainsKey("c"))
        {
            _session.Dispose();
            throw new InvalidDataException("需要 faster-whisper 1.2.1 的 silero_vad_v6.onnx 模型。");
        }
    }

    public float Probability(float[] frame)
    {
        if (frame.Length != 512) throw new ArgumentException("Expected 512 samples.", nameof(frame));
        // The offline Silero asset is trained on bounded contexts. Sustaining
        // its recurrent state through minutes of game BGM can suppress speech.
        // Bound state lifetime while retaining the 64-sample acoustic context.
        if (_frames++ % 128 == 0) { Array.Clear(_h); Array.Clear(_c); }
        Array.Copy(frame, 0, _input, 64, 512);
        var inputs = new[] {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(_input, new[] { 1, 576 })),
            NamedOnnxValue.CreateFromTensor("h", new DenseTensor<float>(_h, new[] { 1, 1, 128 })),
            NamedOnnxValue.CreateFromTensor("c", new DenseTensor<float>(_c, new[] { 1, 1, 128 }))
        };
        using var outputs = _session.Run(inputs);
        var values = outputs.ToArray();
        var probability = values[0].AsTensor<float>().First();
        _h = values[1].AsTensor<float>().ToArray();
        _c = values[2].AsTensor<float>().ToArray();
        Array.Copy(frame, 448, _input, 0, 64);
        return probability;
    }

    public static string ModelPath()
    {
        var installed = Path.Combine(AppPaths.Models, "vad", "silero_vad_v6.onnx");
        if (File.Exists(installed)) return installed;
        var bundled = Path.Combine(AppPaths.Root, ".venv", "Lib", "site-packages", "faster_whisper", "assets", "silero_vad_v6.onnx");
        if (File.Exists(bundled)) return bundled;
        throw new FileNotFoundException("未找到 Silero v6 模型，请安装本地识别依赖，或关闭实验性语音分段。");
    }

    public void Dispose() => _session.Dispose();
}
