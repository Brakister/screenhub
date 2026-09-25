using System;
using System.Collections.Generic;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using ScreenLab.Services;

namespace ScreenLab.Recognition;

/// <summary>
/// Reconhecimento facial por embeddings (SFace 128-d ou ArcFace 512-d via ONNX).
/// Detecta automaticamente a dimensão do embedding pelo modelo carregado.
/// Suporta SFace (entrada [0,255]) e ArcFace (entrada normalizada [-1,1]).
/// </summary>
public sealed class FaceRecognizerService : IDisposable
{
    // --- Configuração por tipo de modelo ---
    private enum ModelType { Unknown, SFace, ArcFace }

    private readonly string _modelPath;
    private readonly object _lock = new();
    private Net? _net;
    private bool _loadFailed;
    private ModelType _modelType = ModelType.Unknown;
    private int _embeddingDim = 0;
    private float _inputScale = 1.0f;
    private Scalar _inputMean = Scalar.All(0);
    private float _minimumCosine = 0.363f;
    private float _minimumMargin = 0.06f;
    private float _selfSimilarityFactor = 0.80f;
    private int _maxPosesPerUser = 20;

    /// <summary>Galeria: nome do usuário → várias poses L2-normalizadas.</summary>
    private readonly Dictionary<string, List<float[]>> _gallery = new(StringComparer.OrdinalIgnoreCase);

    public FaceRecognizerService(string modelPath)
    {
        _modelPath = modelPath;
    }

    /// <summary>True quando o modelo carregou e dá pra reconhecer.</summary>
    public bool IsReady
    {
        get
        {
            lock (_lock) { EnsureLoadedLocked(); return _net != null; }
        }
    }

    public int EmbeddingDim
    {
        get
        {
            lock (_lock) { EnsureLoadedLocked(); return _embeddingDim; }
        }
    }

    public string ModelTypeName => _modelType.ToString();

    public void ResetGallery(IEnumerable<KeyValuePair<string, List<float[]>>> templates)
    {
        lock (_gallery)
        {
            _gallery.Clear();
            foreach (var (name, embeddings) in templates)
            {
                var normalized = new List<float[]>();
                foreach (var embedding in embeddings)
                {
                    var v = Normalize(embedding);
                    if (v != null) normalized.Add(v);
                }
                if (normalized.Count > 0)
                    _gallery[name] = normalized;
            }
        }
        LoggerService.Info($"Galeria facial resetada: {DescribeGallery()}");
    }

    /// <summary>Acrescenta uma pose ao usuário, mantendo o limite de poses.</summary>
    public bool UpsertTemplate(string name, float[] embedding)
    {
        var normalized = Normalize(embedding);
        if (normalized == null)
            return false;

        lock (_gallery)
        {
            if (!_gallery.TryGetValue(name, out var templates))
                _gallery[name] = templates = new List<float[]>();

            // Compara com toda a janela, não só com a última pose.
            if (templates.Any(template => Cosine(template, normalized) > 0.995f))
                return false;

            templates.Add(normalized);
            if (templates.Count > _maxPosesPerUser)
                templates.RemoveRange(0, templates.Count - _maxPosesPerUser);
            return true;
        }
    }

    /// <summary>Info de diagnóstico.</summary>
    public string DescribeGallery()
    {
        lock (_gallery)
        {
            if (_gallery.Count == 0) return "galeria vazia";
            return string.Join(", ", _gallery.Select(g =>
                $"{g.Key}:{g.Value.Count} poses (auto={SelfSimilarity(g.Value):F3})"));
        }
    }

    /// <summary>
    /// Gera o embedding do rosto alinhado (112x112).
    /// Retorna null se modelo não estiver disponível ou falhar.
    /// </summary>
    public float[]? EmbedFace(Mat face)
    {
        if (face.Empty()) return null;

        lock (_lock)
        {
            EnsureLoadedLocked();
            Net? net = _net;
            if (net == null) return null;

            try
            {
                using var sized = new Mat();
                Cv2.Resize(face, sized, new OpenCvSharp.Size(112, 112));

                // Pré-processamento específico por tipo de modelo
                using var blob = CvDnn.BlobFromImage(sized, _inputScale,
                    new OpenCvSharp.Size(112, 112), _inputMean, true, false);

                net.SetInput(blob, "");
                using var output = net.Forward();

                int total = checked((int)output.Total());
                if (total != _embeddingDim) return null;

                var data = new float[total];
                System.Runtime.InteropServices.Marshal.Copy(output.Data, data, 0, total);
                return Normalize(data);
            }
            catch (Exception ex)
            {
                LoggerService.Warn($"Falha ao gerar embedding: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Identifica o rosto conhecido mais próximo.</summary>
    public (string Name, float Confidence)? Recognize(Mat face)
    {
        var emb = EmbedFace(face);
        return emb == null ? null : RecognizeEmbedding(emb);
    }

    /// <summary>Casa um embedding contra a galeria.</summary>
    public (string Name, float Confidence)? RecognizeEmbedding(float[] embedding)
    {
        if (embedding is not { Length: > 0 }) return null;
        if (!IsUsable(embedding)) return null;

        lock (_gallery)
        {
            if (_gallery.Count == 0) return null;

            string? best = null;
            float bestCos = -1f;
            float secondCos = -1f;
            float bestSelf = 0f;

            foreach (var (name, templates) in _gallery)
            {
                float userBest = -1f;
                foreach (var template in templates)
                {
                    float cos = Cosine(embedding, template);
                    if (cos > userBest) userBest = cos;
                }
                if (userBest < 0f) continue;

                if (userBest > bestCos)
                {
                    secondCos = bestCos;
                    bestCos = userBest;
                    best = name;
                    bestSelf = SelfSimilarity(templates);
                }
                else if (userBest > secondCos)
                {
                    secondCos = userBest;
                }
            }

            if (best == null) return null;
            if (bestCos < _minimumCosine) return null;
            if (bestCos - secondCos < _minimumMargin) return null;

            if (bestSelf > 0f && bestCos < bestSelf * _selfSimilarityFactor) return null;

            return (best, bestCos);
        }
    }

    private static bool IsUsable(float[] v)
    {
        double sumSq = 0;
        for (int i = 0; i < v.Length; i++)
        {
            if (!float.IsFinite(v[i])) return false;
            sumSq += (double)v[i] * v[i];
        }
        return sumSq > 1e-6;
    }

    private static float SelfSimilarity(List<float[]> templates)
    {
        if (templates.Count < 2) return 0f;
        float sum = 0f;
        int pairs = 0;
        for (int i = 0; i < templates.Count; i++)
            for (int j = i + 1; j < templates.Count; j++)
            {
                sum += Cosine(templates[i], templates[j]);
                pairs++;
            }
        return pairs == 0 ? 0f : sum / pairs;
    }

    private static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return -1f;
        float dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot; // ambos já L2-normalizados
    }

    private float[]? Normalize(float[] v)
    {
        if (v is not { Length: > 0 }) return null;
        double sumSq = 0;
        foreach (var f in v)
        {
            if (!float.IsFinite(f)) return null;
            sumSq += (double)f * f;
        }
        double norm = Math.Sqrt(sumSq);
        if (norm < 1e-6) return null;
        var r = new float[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = (float)(v[i] / norm);
        return r;
    }

    private void EnsureLoadedLocked()
    {
        if (_net != null || _loadFailed) return;
        try
        {
            _net = CvDnn.ReadNetFromOnnx(_modelPath);
            
            // Detecta tipo de modelo pelo output dimension
            using var testInput = new Mat(112, 112, MatType.CV_8UC3, Scalar.All(128));
            using var blob = CvDnn.BlobFromImage(testInput, 1.0f, new OpenCvSharp.Size(112, 112), Scalar.All(0), true, false);
            _net.SetInput(blob);
            using var testOutput = _net.Forward();
            int outputDim = checked((int)testOutput.Total());

            if (outputDim == 512)
            {
                _modelType = ModelType.ArcFace;
                _embeddingDim = 512;
                _inputScale = 1.0f / 127.5f;  // normaliza para [-1, 1]
                _inputMean = new Scalar(127.5, 127.5, 127.5);
                _minimumCosine = 0.45f;       // ArcFace tem cossenos maiores
                _minimumMargin = 0.08f;
                _selfSimilarityFactor = 0.85f;
                LoggerService.Info($"Modelo ArcFace detectado (512-d): {_modelPath}");
            }
            else if (outputDim == 128)
            {
                _modelType = ModelType.SFace;
                _embeddingDim = 128;
                _inputScale = 1.0f;           // SFace usa [0,255]
                _inputMean = Scalar.All(0);
                _minimumCosine = 0.363f;
                _minimumMargin = 0.06f;
                _selfSimilarityFactor = 0.80f;
                LoggerService.Info($"Modelo SFace detectado (128-d): {_modelPath}");
            }
            else
            {
                _modelType = ModelType.Unknown;
                _embeddingDim = outputDim;
                LoggerService.Warn($"Modelo desconhecido ({outputDim}-d): {_modelPath}");
            }
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            LoggerService.Warn($"Modelo de reconhecimento não carregou: {_modelPath} ({ex.Message})");
        }
    }

    public void Dispose()
    {
        lock (_lock) { _net?.Dispose(); _net = null; }
    }
}