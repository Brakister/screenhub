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
    // 0.363 é o valor recomendado pelo SFace e serve para distinguir UMA pessoa.
    // Com duas ou mais, ele fica ABAIXO do cosseno que pessoas diferentes já
    // alcançam entre si (medido: 0,376 entre Victor e Ailton), e aí uma pessoa
    // passa a "vencer" a outra. 0,42 fica acima do pior caso observado e abaixo
    // do pior match legítimo (0,474). Ajustável na UI, com diagnóstico ao vivo.
    private float _minimumCosine = 0.42f;
    private float _minimumMargin = 0.06f;
    private float _selfSimilarityFactor = 0.80f;

    /// <summary>Teto de poses por pessoa. Espelha UserManager.MaxFaceTemplatesPerUser.</summary>
    public const int MaxPosesPerUser = 100;
    private int _maxPosesPerUser = MaxPosesPerUser;

    // --- Auto-enrollment adaptativo (aprendizado por repetição) ---
    private float _autoEnrollMinCosine = 0.55f;
    private int _autoEnrollMinConsecutive = 3;
    private int _autoEnrollCooldownMs = 30_000;
    private int _autoEnrollBackoffMs = 120_000;
    // "Já tenho esse rosto": rejeita qualquer pose que seja parecida demais com
    // ALGUMA que já existe. Medido nas tuas galerias, poses da mesma pessoa
    // ficam entre 0,64 (ângulo bem diferente) e 0,93 (mesmo ângulo). Então 0,90
    // separa "mesmo ângulo de novo" de "ângulo novo de verdade" — que é
    // exatamente o que faz as 100 poses valerem a pena em vez de encher de
    // cópia. Com 0,995 (o valor anterior) 100 poses viravam 100 quase-cópias.
    private float _autoEnrollDuplicateCosine = 0.90f;
    private volatile bool _autoEnrollEnabled = true;
    private readonly Dictionary<string, int> _consecutiveHits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _nextEligibleAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _capLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Galeria: nome do usuário → várias poses L2-normalizadas.</summary>
    private readonly Dictionary<string, List<float[]>> _gallery = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SelfSimilarity é O(n²): com 100 poses são ~5.000 pares por usuário, e ela
    /// roda a cada reconhecimento. Guardar o resultado e invalidar só quando a
    /// galeria muda evita pagar isso 22x por segundo.
    /// </summary>
    private readonly Dictionary<string, float> _selfSimilarityCache = new(StringComparer.OrdinalIgnoreCase);

    private void InvalidateSelfSimilarity()
    {
        lock (_gallery)
        {
            _selfSimilarityCache.Clear();
        }
    }

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

    public void ConfigureAutoEnrollment(float minCosine = 0.55f, int minConsecutive = 3,
        int cooldownMs = 30_000, int backoffMs = 120_000, float noveltyCosine = 0.90f)
    {
        _autoEnrollMinCosine = minCosine;
        _autoEnrollMinConsecutive = minConsecutive;
        _autoEnrollCooldownMs = Math.Max(5_000, cooldownMs);
        _autoEnrollBackoffMs = Math.Max(_autoEnrollCooldownMs, backoffMs);
        _autoEnrollDuplicateCosine = Math.Clamp(noveltyCosine, 0.50f, 0.999f);
        _autoEnrollEnabled = true;
        LoggerService.Info($"Auto-enrollment configurado: minCosine={minCosine:F2}, minConsecutive={minConsecutive}, cooldown={_autoEnrollCooldownMs}ms, backoff={_autoEnrollBackoffMs}ms, novelty={_autoEnrollDuplicateCosine:F2}, limite={_maxPosesPerUser} poses");
    }

    public float AutoEnrollNoveltyCosine => _autoEnrollDuplicateCosine;

    public void DisableAutoEnrollment()
    {
        _autoEnrollEnabled = false;
        ResetAutoEnrollmentCounters();
        LoggerService.Info("Auto-enrollment desabilitado");
    }

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
            // Re-cadastro manual pode ter libertado espaço: limpa o aviso de
            // limite para que ele volte a valer no próximo preenchimento.
            _capLogged.Clear();
            _selfSimilarityCache.Clear();
            ResetAutoEnrollmentCounters();
        }
        LoggerService.Info($"Galeria facial resetada: {DescribeGallery()}");
    }

    /// <summary>Info de diagnóstico.</summary>
    public string DescribeGallery()
    {
        lock (_gallery)
        {
            if (_gallery.Count == 0) return "galeria vazia";
            return string.Join(", ", _gallery.Select(g =>
                $"{g.Key}:{g.Value.Count} poses (auto={SelfSimilarityCached(g.Key, g.Value):F3})"));
        }
    }

    /// <summary>Como as galerias se separam entre si, e se o limiar é seguro.</summary>
    public sealed record GalleryDiagnostic(
        int PoseCount,
        float WorstIntraCosine,
        float WorstInterCosine,
        bool ThresholdIsSafe,
        string Verdict);

    /// <summary>
    /// Mede, a partir das poses já gravadas, o pior cosseno entre pessoas
    /// diferentes e o pior cossino de um match legítimo da mesma pessoa
    /// (leave-one-out). É o que revela a confusão "eu virei fulano": se o
    /// limiar estiver abaixo do cosseno entre pessoas, uma pessoa sempre vence
    /// a outra. Custo O(n²) e não deve ser chamado por frame.
    /// </summary>
    public GalleryDiagnostic? DiagnoseGallery()
    {
        lock (_gallery)
        {
            var users = _gallery.Where(g => g.Value.Count >= 2).ToList();
            if (users.Count == 0) return null;

            // Pior match da mesma pessoa, sem contar a própria pose consigo mesma.
            var worstIntra = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, templates) in users)
            {
                float worst = float.MaxValue;
                for (int i = 0; i < templates.Count; i++)
                {
                    float bestOther = -1f;
                    for (int j = 0; j < templates.Count; j++)
                    {
                        if (i == j) continue;
                        float c = Cosine(templates[i], templates[j]);
                        if (c > bestOther) bestOther = c;
                    }
                    if (bestOther >= 0f && bestOther < worst) worst = bestOther;
                }
                worstIntra[name] = worst == float.MaxValue ? 0f : worst;
            }

            float worstInter = 0f;
            for (int u = 0; u < users.Count; u++)
            {
                for (int v = u + 1; v < users.Count; v++)
                {
                    foreach (var a in users[u].Value)
                        foreach (var b in users[v].Value)
                        {
                            float c = Cosine(a, b);
                            if (c > worstInter) worstInter = c;
                        }
                }
            }

            float worstIntraAll = worstIntra.Count > 0 ? worstIntra.Values.Min() : 0f;
            bool safe = _minimumCosine > worstInter;

            string verdict = users.Count < 2
                ? "so uma pessoa na galeria: nao ha confusao possivel"
                : safe
                    ? $"limiar {_minimumCosine:F3} esta acima do pior caso entre pessoas diferentes ({worstInter:F3})"
                    : $"ATENCAO: limiar {_minimumCosine:F3} esta ABAIXO do pior caso entre pessoas diferentes ({worstInter:F3}). Uma pessoa pode ser identificada como a outra.";

            return new GalleryDiagnostic(
                users.Sum(u => u.Value.Count), worstIntraAll, worstInter, safe, verdict);
        }
    }

    public float MinimumCosine => _minimumCosine;

    public void SetMinimumCosine(float value)
    {
        lock (_gallery)
        {
            _minimumCosine = Math.Clamp(value, 0.20f, 0.90f);
        }
        LoggerService.Info($"Limiar de reconhecimento ajustado para {_minimumCosine:F3}");
    }

    public void ResetAutoEnrollmentCounters()
    {
        _consecutiveHits.Clear();
        _nextEligibleAt.Clear();
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
                    bestSelf = SelfSimilarityCached(name, templates);
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

            // Deliberadamente NÃO decide aprendizado aqui. O vencedor de um
            // único quadro é instável: quando duas pessoas se parecem, ele
            // alterna entre elas a cada frame. Aprender a partir disso cria um
            // loop de reforço — a pose errada entra na galeria errada, o que
            // torna o erro mais forte no frame seguinte. O aprendizado é
            // decidido por quem já confirmou a identidade estável.
            return (best, bestCos);
        }
    }

    /// <summary>Decisão do auto-enrollment para um embedding reconhecido.</summary>
    public enum AutoEnrollDecision
    {
        /// <summary>Ainda não é hora (contador ou cooldown em andamento).</summary>
        TooEarly,
        /// <summary>A pose é praticamente idêntica a outra já salva.</summary>
        Duplicate,
        /// <summary>A galeria do usuário já está cheia: nada é escrito.</summary>
        AtCapacity,
        /// <summary>Vale aprender — quem enfileira decide quando persistir.</summary>
        Accepted,
    }

    /// <summary>
    /// Avalia se o embedding vale ser aprendido, sem tocar na galeria nem no
    /// disco. Quem chama faz o commit (memória + disco) num único lugar
    /// serializado, o que garante que os dois nunca divirjam.
    /// </summary>
    public AutoEnrollDecision EvaluateAutoEnroll(string name, float[] embedding, float cosine)
    {
        if (!_autoEnrollEnabled) return AutoEnrollDecision.TooEarly;
        if (cosine < _autoEnrollMinCosine) return AutoEnrollDecision.TooEarly;

        lock (_gallery)
        {
            _gallery.TryGetValue(name, out var templates);

            // learned: a galeria já cobre bem esse rosto. Não insere nada e não
            // reescreve o config.json — é o caso que garante "já aprendeu, não
            // reaprende". Enche as 20 poses e para; só volta por re-cadastro.
            if (templates != null && templates.Count >= _maxPosesPerUser)
            {
                if (!_capLogged.TryGetValue(name, out _))
                {
                    _capLogged[name] = true;
                    LoggerService.Info($"[Auto-enroll] '{name}' atingiu o limite de {_maxPosesPerUser} poses. Auto-enrollment pausado até o cadastro ser refeito.");
                }
                _consecutiveHits[name] = 0;
                return AutoEnrollDecision.AtCapacity;
            }

            int hits = _consecutiveHits.GetValueOrDefault(name, 0) + 1;
            _consecutiveHits[name] = hits;
            if (hits < _autoEnrollMinConsecutive) return AutoEnrollDecision.TooEarly;

            // Cobre tanto o intervalo entre poses aceitas quanto o back-off
            // aplicado depois de uma rejeição.
            if (_nextEligibleAt.TryGetValue(name, out var next) && DateTime.UtcNow < next)
                return AutoEnrollDecision.TooEarly;

            var normalized = Normalize(embedding);
            if (normalized == null) return AutoEnrollDecision.TooEarly;

            // Quadro praticamente idêntico a uma pose já salva: rejeita e faz
            // back-off. Antes o contador zerava aqui e a tentativa se repetia
            // indefinidamente, a cada 3 reconhecimentos, sem nunca parar.
            if (templates != null && templates.Any(t => Cosine(t, normalized) > _autoEnrollDuplicateCosine))
            {
                _consecutiveHits[name] = 0;
                _nextEligibleAt[name] = DateTime.UtcNow.AddMilliseconds(_autoEnrollBackoffMs);
                return AutoEnrollDecision.Duplicate;
            }

            _consecutiveHits[name] = 0;
            _nextEligibleAt[name] = DateTime.UtcNow.AddMilliseconds(_autoEnrollCooldownMs);
            return AutoEnrollDecision.Accepted;
        }
    }

    /// <summary>
    /// Grava a pose na galeria em memória, respeitando o limite por usuário.
    /// Devolve false quando nada mudou — nesse caso o chamador NÃO deve tocar
    /// no disco, e é isso que mantém o volume de escrita perto de zero.
    /// </summary>
    public bool CommitTemplate(string name, float[] embedding)
    {
        var normalized = Normalize(embedding);
        if (normalized == null) return false;

        lock (_gallery)
        {
            if (!_gallery.TryGetValue(name, out var templates))
                _gallery[name] = templates = new List<float[]>();

            if (templates.Count >= _maxPosesPerUser) return false;
            if (templates.Any(t => Cosine(t, normalized) > _autoEnrollDuplicateCosine)) return false;

            templates.Add(normalized);
            _selfSimilarityCache.Remove(name);
            return true;
        }
    }

    /// <summary>Descarta da galeria em memória a pose que falhou ao ir para o disco.</summary>
    public void RollbackTemplate(string name, float[] embedding)
    {
        var normalized = Normalize(embedding);
        if (normalized == null) return;

        lock (_gallery)
        {
            if (_gallery.TryGetValue(name, out var templates))
            {
                int idx = templates.FindIndex(t => Cosine(t, normalized) > 0.9999f);
                if (idx >= 0)
                {
                    templates.RemoveAt(idx);
                    _selfSimilarityCache.Remove(name);
                }
            }
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

    /// <summary>SelfSimilarity memoizada por usuário. Exige o lock da galeria.</summary>
    private float SelfSimilarityCached(string name, List<float[]> templates)
    {
        if (_selfSimilarityCache.TryGetValue(name, out float cached)) return cached;
        float computed = SelfSimilarity(templates);
        _selfSimilarityCache[name] = computed;
        return computed;
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
            Net? net = CvDnn.ReadNetFromOnnx(_modelPath);
            if (net == null)
                throw new InvalidOperationException("ReadNetFromOnnx não retornou uma rede.");
            _net = net;

            // Detecta tipo de modelo pelo output dimension
            using var testInput = new Mat(112, 112, MatType.CV_8UC3, Scalar.All(128));
            using var blob = CvDnn.BlobFromImage(testInput, 1.0f, new OpenCvSharp.Size(112, 112), Scalar.All(0), true, false);
            net.SetInput(blob);
            using var testOutput = net.Forward();
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