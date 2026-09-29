using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using ScreenLab.Recognition;
using ScreenLab.Services;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace ScreenLab.Capture;

/// <summary>
/// Motor de captura 24/7: lê a câmera em uma thread de fundo, aplica os modos
/// de disparo (rosto, palma, intervalo e manual), carimba a foto com
/// data/hora + usuário e salva em disco. Projetado para baixo consumo.
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private const int DetectionWidth = 320;
    private const int DetectionHeight = 240;
    private const int YuNetInput = 320;   // entrada quadrada do YuNet
    private const float YuNetMinScore = 0.60f;
    private const float FaceNmsIou = 0.30f;
    private const int PreviewWidth = 800;   // pré-visualização da peça em resolução reduzida
    private const int PreviewHeight = 450;
    private const int FacePreviewWidth = 640;
    private const int FacePreviewHeight = 480;
    private const int PreviewMinIntervalMs = 33; // ~30 fps preview
    private const int LoopTargetMsWithPreview = 33; // ~30 fps: o preview não pode ser mais lento que a câmera
    private const int LoopPauseMsWithoutPreview = 90; // oculto: ~10 leituras/s p/ economizar CPU
    /// <summary>
    /// Cadência das caixas verdes no preview do rosto. Independente do
    /// <see cref="AppConfig.FaceIntervalMs"/>, que cronometra o RECONHECIMENTO
    /// (YuNet + SFace, caro). Com os dois atrelados, o quadrado andava 2,5x por
    /// segundo (400 ms) mesmo com o vídeo a 30 fps — vídeo novo com moldura
    /// velha é exatamente a sensação de "lag".
    /// </summary>
    private const int FaceBoxRefreshMs = 100;
    /// <summary>Espera máxima pelo quadro do rosto quando algo depende dele agora.</summary>
    private const int FaceFrameWaitMs = 400;
    /// <summary>Taxa pedida às duas webcams. 30 fps é o teto da maioria delas e
    /// o que o app Câmera do Windows entrega.</summary>
    private const int TargetCameraFps = 30;
    /// <summary>Abaixo disso o preview fica visivelmente travado, então vale avisar.</summary>
    private const int MinAcceptableCameraFps = 20;

    private readonly AppConfig _config;
    private readonly UserManager _users;
    private readonly FaceRecognizerService _faceRecognizer;
    private readonly Net? _yunet;
    private bool _yunetInferenceWarningLogged;
    private volatile string _pendingEnrollmentUser = "";
    private volatile bool _pendingEnrollmentCapture;
    private string _enrollmentSessionUser = "";
    private readonly List<float[]> _enrollmentSamples = new();
    private const int EnrollmentSampleCount = 10;
    private const float EnrollmentDuplicateSimilarity = 0.97f;
    private static readonly string[] EnrollmentPrompts =
    {
        "OLHE DE FRENTE", "VIRE PARA A ESQUERDA", "VIRE PARA A DIREITA",
        "INCLINE PARA CIMA", "INCLINE PARA BAIXO",
        "VOLTE PARA FRENTE", "LEVANTE O QUEIXO", "BAIXE O QUEIXO",
        "SORRIA LEVE", "FECHE OS OLHOS",
    };
    private string _lastEnrollmentPrompt = "";
    /// <summary>
    /// Pedido de reinício da sessão. Só a thread da.camera consome: permite
    /// que a UI cancele/recomece sem tocar nas listas que ela não possui.
    /// </summary>
    private volatile bool _pendingEnrollmentReset;
    private DateTime _enrollmentStartedAt = DateTime.MinValue;
    /// <summary>Sem isso um cadastro travado (câmera/modelo) prendia a UI para sempre.</summary>
    private const int EnrollmentTimeoutSeconds = 240;

    // --- Gravação de embeddings fora da thread da câmera -----------------------
    // Escrever config.json leva ~4 ms (medido: 0,96 ms de serializar + 3,7 ms de
    // WriteAllText em %APPDATA%). Feito dentro do loop de captura isso vira um
    // soluço no processamento de frames, então tudo que precisa ir para o disco
    // passa por esta fila, consumida por uma única thread. Serializar as
    // escritas também evita duas threads reescrevendo o mesmo arquivo.
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string User, float[] Embedding)> _pendingWrites = new();
    private readonly AutoResetEvent _writeSignal = new(false);
    private Thread? _writerThread;
    private volatile bool _writerRunning;

    // --- Estabilidade da identidade -------------------------------------------
    // Rosto detectado não é o mesmo que pessoa confirmada. O rastreio exige
    // que a MESMA pessoa apareça por FaceConfirmSeconds antes de virar usuário
    // ativo: recognition de quadro único erra com facilidade (luz, meio
    //virada, alguém passando atrás) e trocar o usuário a cada engano suja as
    // fotos com o nome errado.
    private string _trackedUser = "";
    private DateTime _trackedSince = DateTime.MinValue;
    private string _confirmedUser = "";
    private DateTime _lastSeenAt = DateTime.MinValue;
    private DateTime _lastSampleAt = DateTime.MinValue;

    // Último operador escolhido A MÃO pelo usuário (botões ou combo). O
    // reconhecimento facial também escreve em UserManager.ActiveUser — é assim
    // que as fotos são nomeadas sozinhas — mas essas duas coisas não podem ser a
    // mesma: quando o app erra o nome, o F10 acabava gravando o rosto na
    // galeria errada. O log mostra 3 poses do Victor entrando na galeria do
    // Ailton exatamente assim.
    private volatile string _operatorChosenByUser = "";

    // Quando false, o reconhecimento facial não mexe no operador selecionado: o
    // app passa a só mostrar quem acha que é, mas quem manda no nome das fotos
    // é o usuário. É a saída quando duas pessoas se confundem entre si.
    private volatile bool _autoSwitchOperator = true;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(30);

    // --- Ritmo de captura por rosto -------------------------------------------
    private DateTime _facePresentSince = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _lastPhotoByUser = new(StringComparer.OrdinalIgnoreCase);
    private volatile string _faceCaptureUser = "";

    private readonly object _stateLock = new();
    private VideoCapture? _capture;
    private VideoCapture? _faceCapture;
    private Thread? _thread;
    private Thread? _faceThread;
    private CancellationTokenSource? _cts;
    private int _disposed;

    // Pedido de reabertura vindo de fora (troca de índice). Quem FAZ o descarte
    // é a thread que está lendo: descartar um VideoCapture de dentro de um
    // Read() em voo estoura ObjectDisposedException nela.
    private volatile bool _captureNeedsReopen;
    private volatile bool _faceCaptureNeedsReopen;

    // --- Pump dedicado da câmera do rosto -------------------------------------
    // Antes as DUAS webcams eram lidas no MESMO loop, uma atrás da outra. Cada
    // Read() do MSMF bloqueia até o próximo quadro do dispositivo (~33 ms a
    // 30 fps), então o período do loop virava a SOMA das duas latências: o
    // preview ia a ~15 fps e a câmera do rosto entrava sempre meio quadro
    // atrasada. Aqui ela tem thread própria e nunca espera a principal.
    private readonly object _faceFrameLock = new();
    private Mat? _faceFront;   // último quadro lido, seguro para o consumidor
    private Mat? _faceBack;    // buffer em que o pump escreve
    private long _faceFrameSeq; // quantos quadros o pump já entregou
    private long _faceFrameSeen; // último seq consumido pelo loop principal
    private long _faceFrames;  // total lido (estatística de fps)
    private volatile bool _faceThreadRunning;

    private readonly Stopwatch _faceWatch = Stopwatch.StartNew();
    private readonly Stopwatch _faceBoxWatch = Stopwatch.StartNew();
    private readonly Stopwatch _previewWatch = Stopwatch.StartNew();
    private readonly Stopwatch _facePreviewWatch = Stopwatch.StartNew();
    private DateTime _lastCapture = DateTime.MinValue;
    private DateTime _lastIntervalCapture = DateTime.MinValue;
    private DateTime _lastCameraFailLog = DateTime.MinValue;
    private DateTime _lastFaceCameraFailLog = DateTime.MinValue;
    private DateTime _nextFaceCameraRetryAt = DateTime.MinValue;
    private int _cameraFailStreak;
    private int _faceCameraFailStreak;
    private Rect[] _lastFaceBoxes = Array.Empty<Rect>();
    private Point2f[][] _lastFaceLandmarks = Array.Empty<Point2f[]>();
    private string[] _lastFaceLabels = Array.Empty<string>();

    // Estatísticas operacionais (relatório periódico no log — útil para 24/7)
    private readonly object _statsLock = new();
    private DateTime _statsSince = DateTime.Now;
    private long _loops, _readMs, _detectMs, _saveMs, _lastCaptureCount;
    private long _lastFaceFramesTotal;
    private long _previewFrames;         // previews publicados na UI
    private long _lastPreviewFramesTotal;
    private volatile string _pendingManualTrigger = "";
    private volatile bool _paused;
    private volatile bool _running;
    private volatile bool _previewWanted = true;

    private static readonly CascadeClassifier? _faceCascade = LoadFaceCascade();
    private static readonly CascadeClassifier? _profileFaceCascade = LoadProfileFaceCascade();
    private static readonly CascadeClassifier? _eyeCascade = LoadEyeCascade();

    public event Action<Mat>? PreviewFrame;       // quadro da câmera da peça
    public event Action<Mat>? FacePreviewFrame;   // quadro da câmera do rosto
    public event Action<string>? PhotoCaptureStarted; // disparado quando a captura começa (LED verde)
    public event Action<CapturedPhoto>? PhotoCaptured;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<bool>? RunningChanged;
    public event Action<string>? FaceEnrollmentCompleted;
    /// <summary>Cancelamento/falha/timeout: (usuário, motivo).</summary>
    public event Action<string, string>? FaceEnrollmentCanceled;
    public event Action<string, int, int, string, int>? FaceEnrollmentProgress;

    /// <summary>Evento disparado quando o aprendizado de rosto atual completa (sucesso ou falha).</summary>
    public event Action<bool, string>? LearnFaceCompleted;

    public CaptureEngine(AppConfig config, UserManager users)
    {
        _config = config;
        _users = users;
        string modelPath = Path.Combine(AppContext.BaseDirectory, "Data", "models",
            "face_recognition_sface_2021dec.onnx");
        _faceRecognizer = new FaceRecognizerService(modelPath);
        _faceRecognizer.ResetGallery(_users.FaceTemplateSetsSnapshot());
        _users.UsersChanged += ReloadFaceGallery;
        LoggerService.Info($"Galeria facial: {_faceRecognizer.DescribeGallery()}");

        string detectorPath = Path.Combine(AppContext.BaseDirectory, "Data", "models",
            "face_detection_yunet_2023mar.onnx");
        try
        {
            _yunet = File.Exists(detectorPath) ? CvDnn.ReadNetFromOnnx(detectorPath) : null;
        }
        catch (Exception ex)
        {
            _yunet = null;
            LoggerService.Warn($"Detector YuNet não carregou: {ex.Message}");
        }
    }

    public bool IsRunning => _running;
    public bool IsPaused => _paused;

    /// <summary>
    /// Chamado pela UI quando o usuário troca o operador no combo ou nos botões.
    /// A partir daqui o F10 sabe para quem o rosto deve ser aprendido, sem
    /// depender do que o reconhecimento facial acha.
    /// </summary>
    public void NotifyOperatorChosenByUser(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            _operatorChosenByUser = name;
    }

    /// <summary>Se o rosto detectado pode trocar o operador selecionado.</summary>
    public bool AutoSwitchOperator
    {
        get => _autoSwitchOperator;
        set => _autoSwitchOperator = value;
    }

    /// <summary>Quem o rosto está sendo identificado como agora ("" se ninguém).</summary>
    public string DetectedUser => _confirmedUser;

    private void ReloadFaceGallery()
    {
        _faceRecognizer.ResetGallery(_users.FaceTemplateSetsSnapshot());
        LoggerService.Info($"Galeria facial atualizada: {_faceRecognizer.DescribeGallery()}");
    }

    public void RequestFaceEnrollment(string user)
    {
        if (string.IsNullOrWhiteSpace(user) ||
            !_users.UserNamesSnapshot().Contains(user, StringComparer.OrdinalIgnoreCase))
            return;

        // A UI só publica a solicitação. A lista de amostras e o restante da
        // sessão são tocados exclusivamente pela thread da câmera, eliminando
        // a corrida que existia quando RequestFaceEnrollment os alterava aqui.
        _pendingEnrollmentUser = user;
        _pendingEnrollmentCapture = false;
        // Recomeçar sempre do zero: sem isso, pedir o cadastro de novo para o
        // mesmo usuário herdaria as poses da tentativa abandonada.
        _pendingEnrollmentReset = true;
    }

    public void CaptureEnrollmentSample()
    {
        if (!string.IsNullOrEmpty(_pendingEnrollmentUser))
            _pendingEnrollmentCapture = true;
    }

    /// <summary>
    /// Encerra o cadastro em andamento. Seguro para chamar da UI: libera o
    /// botão imediatamente e manda a thread da câmera descartar as amostras.
    /// </summary>
    public void CancelFaceEnrollment(string reason = "Cadastro cancelado")
    {
        string user = _pendingEnrollmentUser;
        _pendingEnrollmentCapture = false;
        _pendingEnrollmentUser = "";
        _pendingEnrollmentReset = true;
        if (!string.IsNullOrEmpty(user))
        {
            LoggerService.Info($"Cadastro facial encerrado: {user} ({reason})");
            FaceEnrollmentCanceled?.Invoke(user, reason);
        }
    }

    public void ConfigureAutoEnrollment(float minCosine = 0.55f, int minConsecutive = 3,
        int cooldownMs = 30_000, int backoffMs = 120_000, float noveltyCosine = 0.90f)
    {
        _faceRecognizer.ConfigureAutoEnrollment(minCosine, minConsecutive, cooldownMs, backoffMs, noveltyCosine);
    }

    public void SetMinimumCosine(float value) => _faceRecognizer.SetMinimumCosine(value);

    public void SetMinimumMargin(float value) => _faceRecognizer.SetMinimumMargin(value);

    public float RecognitionMargin => _faceRecognizer.MinimumMargin;

    public FaceRecognizerService.GalleryDiagnostic? DiagnoseFaceGallery() => _faceRecognizer.DiagnoseGallery();

    public float RecognitionThreshold => _faceRecognizer.MinimumCosine;

    public float AutoEnrollNovelty => _faceRecognizer.AutoEnrollNoveltyCosine;

    public void DisableAutoEnrollment()
    {
        _faceRecognizer.DisableAutoEnrollment();
    }

    // ------------------------------------------------- Gravação de embeddings

    private void StartWriter()
    {
        if (_writerRunning) return;
        _writerRunning = true;
        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "ScreenLab.embedding-writer",
        };
        _writerThread.Start();
    }

    /// <summary>
    /// Enfileira uma pose para gravação. Seguro de chamar da thread da câmera:
    /// só faz enqueue + set de um evento.
    /// </summary>
    private void EnqueueEmbeddingWrite(string user, float[] embedding)
    {
        if (string.IsNullOrEmpty(user) || embedding is not { Length: > 0 })
            return;
        _pendingWrites.Enqueue((user, embedding));
        _writeSignal.Set();
    }

    private void WriterLoop()
    {
        while (_writerRunning)
        {
            try
            {
                if (!_pendingWrites.TryDequeue(out var item))
                {
                    _writeSignal.WaitOne(500);
                    continue;
                }

                // Consome o resto de uma vez: gravar cada pose isoladamente
                // reescreveria o mesmo arquivo N vezes sem ganho.
                var batch = new List<(string User, float[] Embedding)> { item };
                while (_pendingWrites.TryDequeue(out var more))
                    batch.Add(more);

                PersistEmbeddingBatch(batch);
            }
            catch (Exception ex)
            {
                LoggerService.Error("Falha na thread de gravação de embeddings", ex);
            }
        }
    }

    private void PersistEmbeddingBatch(List<(string User, float[] Embedding)> batch)
    {
        foreach (var (user, embedding) in batch)
        {
            // Memória primeiro: se a pose for duplicata ou o usuário já estiver
            // no limite, CommitTemplate devolve false e NADA é gravado no disco.
            // É esse desvio que evita a reescrita eterna do config.json.
            if (!_faceRecognizer.CommitTemplate(user, embedding))
                continue;

            if (_users.AddFaceTemplate(user, embedding))
            {
                LoggerService.Info($"[Auto-enroll] Pose de '{user}' salva no disco (total: {_users.FaceTemplateCount(user)} poses)");
            }
            else
            {
                // Não deixa a galeria em memória divergir do config.json: se a
                // escrita falhou, a pose não pode sobreviver ao próximo boot.
                _faceRecognizer.RollbackTemplate(user, embedding);
                LoggerService.Warn($"[Auto-enroll] Falha ao salvar pose de '{user}' no disco; descartada da galeria");
            }
        }
    }

    private void StopWriter()
    {
        _writerRunning = false;
        _writeSignal.Set();
        _writerThread?.Join(1500);
        _writerThread = null;
    }

    public void LearnCurrentFace()
    {
        if (!_running || _paused)
        {
            LearnFaceCompleted?.Invoke(false, "Motor pausado ou parado");
            return;
        }
        _pendingLearnFace = true;
    }

    private volatile bool _pendingLearnFace = false;

    /// <summary>
    /// Encerra uma tentativa de aprendizado com falha, logando o motivo.
    /// Sem o log, "não salvou" é indistinguível de "não tentou": o operador
    /// só via a mensagem na tela, que some em 3 segundos, e o log fica
    /// mudo justamente nos casos que precisam de diagnóstico.
    /// </summary>
    private void FailLearnFace(string reason)
    {
        LoggerService.Warn($"[LearnFace] Não salvou: {reason}");
        LearnFaceCompleted?.Invoke(false, reason);
    }

    private void ProcessLearnCurrentFace(Mat faceFrame)
    {
        if (faceFrame == null || faceFrame.Empty())
        {
            FailLearnFace("Câmera de rosto indisponível");
            return;
        }

        DetectFacesForPreview(faceFrame);
        var faceBoxes = _lastFaceBoxes;
        var faceLandmarks = _lastFaceLandmarks;

        if (faceBoxes.Length == 0)
        {
            FailLearnFace("Nenhum rosto detectado na câmera");
            return;
        }
        if (faceBoxes.Length > 1)
        {
            FailLearnFace($"Múltiplos rostos detectados ({faceBoxes.Length}) — mostre apenas 1");
            return;
        }

        Rect faceRect = ScaleFaceRect(faceBoxes[0], faceFrame.Width, faceFrame.Height);
        if (!FaceQualityOk(faceFrame, faceRect, out string qualityReason))
        {
            FailLearnFace($"Qualidade ruim: {qualityReason}");
            return;
        }

        using var crop = new Mat(faceFrame, faceRect);
        Point2f[]? cropLandmarks = LandmarksToCrop(
            faceLandmarks.Length > 0 ? faceLandmarks[0] : null, faceRect, faceFrame.Width, faceFrame.Height);

        if (!TryAlignFace(crop, cropLandmarks, out Mat aligned))
        {
            FailLearnFace("Falha ao alinhar rosto");
            return;
        }

        using (aligned)
        {
            float[]? embedding = _faceRecognizer.EmbedFace(aligned);
            if (embedding == null)
            {
                FailLearnFace("Modelo de reconhecimento indisponível");
                return;
            }

            // O destino é o operador que o USUÁRIO escolheu, nunca o que o
            // reconhecimento facial deduziu. Se o app acha que quem está na
            // câmera é outra pessoa, usar a dedução dele aqui gravava o teu
            // rosto na galeria errada e reforçava a confusão — que foi
            // exatamente o que aconteceu com o Ailton.
            string target = _operatorChosenByUser.Length > 0
                ? _operatorChosenByUser
                : _users.ActiveUser;

            bool conflict = _confirmedUser.Length > 0
                && !string.Equals(_confirmedUser, target, StringComparison.OrdinalIgnoreCase);
            if (conflict)
            {
                LoggerService.Warn(
                    $"[LearnFace] Rosto na frente da câmera foi identificado como '{_confirmedUser}', "
                    + $"mas o operador escolhido é '{target}'. A pose vai para '{target}' mesmo assim. "
                    + "Se quem está na câmera não for essa pessoa, não use o F10.");
            }

            // Mesmo caminho da fila: memória e disco mudam juntos, e o
            // CommitTemplate recusa duplicata e usuário cheio sem gravar nada.
            if (!_faceRecognizer.CommitTemplate(target, embedding))
            {
                FailLearnFace($"Pose recusada para '{target}' — já está na galeria "
                    + "ou o usuário atingiu o limite de poses");
                return;
            }

            if (_users.AddFaceTemplate(target, embedding))
            {
                int total = _users.FaceTemplateCount(target);
                LoggerService.Info($"[LearnFace] Rosto aprendido para '{target}' (total poses: {total})");
                string msg = $"Rosto aprendido para '{target}' ({total} poses)";
                if (conflict) msg += $" — o app está achando que é '{_confirmedUser}'";
                LearnFaceCompleted?.Invoke(true, msg);
            }
            else
            {
                _faceRecognizer.RollbackTemplate(target, embedding);
                FailLearnFace($"Falha ao salvar no disco a pose de '{target}'");
            }
        }
    }

    private void ResetEnrollmentSession()
    {
        _pendingEnrollmentReset = false;
        _enrollmentSessionUser = "";
        _enrollmentSamples.Clear();
        _lastEnrollmentPrompt = "";
        _enrollmentStartedAt = DateTime.MinValue;
    }

    private static CascadeClassifier? LoadFaceCascade()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Data", "haarcascade_frontalface_default.xml");
            if (!File.Exists(path))
            {
                LoggerService.Warn($"Cascade de rosto não encontrado em: {path}");
                return null;
            }
            return new CascadeClassifier(path);
        }
        catch (Exception ex)
        {
            LoggerService.Error("Falha ao carregar cascade de rosto", ex);
            return null;
        }
    }

    private static CascadeClassifier? LoadProfileFaceCascade()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Data", "haarcascade_profileface.xml");
            return File.Exists(path) ? new CascadeClassifier(path) : null;
        }
        catch (Exception ex)
        {
            LoggerService.Error("Falha ao carregar cascade de perfil facial", ex);
            return null;
        }
    }

    private static CascadeClassifier? LoadEyeCascade()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Data", "haarcascade_eye.xml");
            return File.Exists(path) ? new CascadeClassifier(path) : null;
        }
        catch (Exception ex)
        {
            LoggerService.Error("Falha ao carregar cascade de olhos", ex);
            return null;
        }
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_running) return;
            if (_thread is { IsAlive: true })
            {
                LoggerService.Warn("A thread anterior ainda não encerrou; novo início recusado.");
                StatusChanged?.Invoke("Aguarde a câmera anterior encerrar antes de reiniciar");
                return;
            }

            _running = true;
            _lastIntervalCapture = DateTime.Now;
            _cts = new CancellationTokenSource();
            // Início limpo: um pedido de reabertura pendente de uma sessão
            // anterior derrubaria a câmera que acabou de abrir.
            _captureNeedsReopen = false;
            _faceCaptureNeedsReopen = false;
        }

        ResetFaceRuntimeState();

        _thread = new Thread(Loop) { IsBackground = true, Name = "ScreenLab.Capture" };
        _thread.Start();
        StartFacePump();
        StartWriter();
        LoggerService.Info("Motor de captura iniciado");
        StatusChanged?.Invoke("Iniciando câmera...");
        RunningChanged?.Invoke(true);
    }

    public bool Stop()
    {
        Thread? thread;
        Thread? faceThread;
        VideoCapture? capture;
        VideoCapture? faceCapture;
        lock (_stateLock)
        {
            thread = _thread;
            faceThread = _faceThread;
            if (!_running && (thread == null || !thread.IsAlive))
                return true;

            _running = false;
            _cts?.Cancel();
            capture = _capture;
            faceCapture = _faceCapture;
            _capture = null;
            _faceCapture = null;
        }
        _faceThreadRunning = false;

        // Encerra as threads ANTES de derrubar as câmeras. Read() do MSMF
        // bloqueia até no máximo um intervalo de quadro (~33ms a 30fps), então
        // o Join é curto — e ordenar assim tira o descarte de cima do
        // Read() em voo, que é a mesma corrida que SetCameraIndices tinha.
        bool faceStopped = faceThread == null || !faceThread.IsAlive || faceThread.Join(3000);
        bool stopped = thread == null || !thread.IsAlive || thread.Join(5000);

        // Um pedido de reabertura que sobrou vira lixo aqui: as threads já
        // morreram, então ninguém vai aplicá-lo.
        _captureNeedsReopen = false;
        _faceCaptureNeedsReopen = false;
        capture?.Dispose();
        faceCapture?.Dispose();

        if (!faceStopped || !stopped)
        {
            LoggerService.Warn("A thread de captura não encerrou em 5 s; um novo início será recusado.");
            StatusChanged?.Invoke("A câmera ainda está encerrando; aguarde");
            RunningChanged?.Invoke(false);
            return false;
        }

        // Espera a fila de embeddings drenar antes de derrubar o motor: uma
        // pose já em memória e ainda não gravada se perderia no restart.
        StopWriter();

        lock (_stateLock)
        {
            _cts?.Dispose();
            _cts = null;
            _thread = null;
            _faceThread = null;
            _frameBuf?.Dispose();
            _frameBuf = null;
            _faceFrameBuf?.Dispose();
            _faceFrameBuf = null;
            _previewBuf?.Dispose();
            _previewBuf = null;
            _facePreviewBuf?.Dispose();
            _facePreviewBuf = null;
            lock (_faceFrameLock)
            {
                _faceFront?.Dispose();
                _faceFront = null;
                _faceBack?.Dispose();
                _faceBack = null;
            }
        }

        LoggerService.Info("Motor de captura parado");
        StatusChanged?.Invoke("Parado");
        RunningChanged?.Invoke(false);
        return true;
    }

    /// <summary>Reabre a câmera (usado ao trocar o índice da câmera).</summary>
    public void Restart()
    {
        if (Stop())
            Start();
    }

    /// <summary>
    /// Troca o índice da câmera principal/face sem reiniciar a thread de captura.
    /// Muito mais rápido que Restart() pois não espera a thread morrer/nascer.
    /// </summary>
    public void SetCameraIndices(int cameraIndex, int faceCameraIndex)
    {
        _config.CameraIndex = cameraIndex;
        _config.FaceCameraIndex = faceCameraIndex;

        // Invalida as capturas atuais; o loop vai reabrir com os novos índices
        // no próximo ciclo.
        //
        // O descarte é adiado para a thread que LÊ (o loop e o pump), em vez de
        // ser feito aqui. Descartar um VideoCapture enquanto um Read() está em
        // voo é uma corrida: pode perder a iteração em curso no loop — que é
        // onde o aprendizado de rosto pedido pelo operador é processado. No log
        // antigo, ObjectDisposedException aparecia sistematicamente no mesmo
        // segundo das trocas de índice. (Um probe isolado não reproduziu a
        // exceção neste caminho do MSMF, então isto é defesa de corrida, não
        // reprodução do bug.)
        lock (_stateLock)
        {
            _captureNeedsReopen = true;
            _faceCaptureNeedsReopen = true;
            _nextFaceCameraRetryAt = DateTime.MinValue;
        }
        // O quadro em mãos é da câmera antiga: descartá-lo evita um flash da
        // câmera errada no preview logo depois da troca. O pump pode estar
        // lendo _faceBack agora, então só o front (que é só leitura) sai.
        lock (_faceFrameLock)
        {
            _faceFront?.Dispose();
            _faceFront = null;
            _faceFrameSeq = 0;
        }
        LoggerService.Info($"Índices de câmera alterados: principal={cameraIndex}, face={faceCameraIndex}");
        StatusChanged?.Invoke($"Câmera principal: {cameraIndex} | Câmera face: {faceCameraIndex}");
    }

    /// <summary>
    /// Invalida confirmação e dwell quando a escolha manual do usuário muda.
    /// O cooldown por pessoa é preservado entre mudanças e reinícios do motor.
    /// </summary>
    public void ResetIdentityForUserSelection() => ResetFaceRuntimeState();

    public void SetPreviewWanted(bool wanted) => _previewWanted = wanted;

    /// <summary>Buffers reutilizados (evita alocar/liberar memória a cada frame).</summary>
    private Mat? _frameBuf;
    private Mat? _faceFrameBuf;
    private Mat? _previewBuf;
    private Mat? _facePreviewBuf;

    private Mat FrameBuffer => _frameBuf ??= new Mat();
    private Mat FaceFrameBuffer => _faceFrameBuf ??= new Mat();
    private Mat PreviewBuffer => _previewBuf ??= new Mat();
    private Mat FacePreviewBuffer => _facePreviewBuf ??= new Mat();

    public void SetPaused(bool paused)
    {
        _paused = paused;
        StatusChanged?.Invoke(paused ? "Pausado" : "Em execução");
        LoggerService.Info(paused ? "Captura pausada" : "Captura retomada");
    }

    public void TogglePaused() => SetPaused(!_paused);

    /// <summary>Dispara uma captura imediata (botão, hotkey ou bandeja).</summary>
    public void CaptureNow(string trigger = "Manual")
    {
        if (!_running || _paused)
            return;
        _pendingManualTrigger = trigger;
    }

    /// <summary>Captura a peça com uma janela da câmera do rosto no quadro.</summary>
    public void CaptureWithFaceNow(string trigger = "Rosto manual")
    {
        if (!_running || _paused)
            return;
        _pendingManualTrigger = trigger;
    }

    /// <summary>Captura forçando inclusão do rosto (usado pela tecla P).</summary>
    public void CaptureWithFaceForced(string trigger = "Manual c/ Rosto")
    {
        if (!_running || _paused)
            return;
        _pendingManualTrigger = trigger;
        _forceFaceInNextCapture = true;
    }

    private volatile bool _forceFaceInNextCapture = false;

    public void ResetIntervalTimer() => _lastIntervalCapture = DateTime.Now;

    public static bool CameraAvailable(int index)
    {
        try
        {
            using var cap = new VideoCapture(index);
            return cap.IsOpened();
        }
        catch
        {
            return false;
        }
    }

    public static string DescribeCamera(int index)
    {
        try
        {
            using var cap = new VideoCapture(index);
            if (!cap.IsOpened()) return "indisponível";
            return $"{cap.FrameWidth}x{cap.FrameHeight} ({cap.Get(VideoCaptureProperties.Fps):0} fps)";
        }
        catch
        {
            return "indisponível";
        }
    }

    private void Loop()
    {
        var ct = _cts!.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!EnsureCameraOpen())
                {
                    // Backoff: tenta rápido no início, depois espaça para poupar CPU.
                    Thread.Sleep(_cameraFailStreak > 6 ? 10000 : 2000);
                    continue;
                }

                var capture = _capture!;

                if (_paused)
                {
                    Thread.Sleep(150);
                    continue;
                }

                var frame = FrameBuffer;
                var loopStart = Stopwatch.StartNew();

                // --- LEITURA DA CÂMERA PRINCIPAL (sempre) ---
                var swRead = Stopwatch.StartNew();
                bool readOk = capture.Read(frame) && !frame.Empty();

                if (!readOk)
                {
                    swRead.Stop();
                    // Câmera desconectada? Tenta reabrir no próximo ciclo.
                    TryDisposeCapture();
                    Thread.Sleep(500);
                    continue;
                }

                // --- QUADRO DA CÂMERA DO ROSTO ---------------------------------
                // Já vem pronto da thread dedicada (ver FaceCameraLoop): não
                // esperamos o FaceIntervalMs para ler, e não serializamos a
                // leitura atras da principal. O throttle de 400 ms é do
                // RECONHECIMENTO, não da leitura — atrelar os dois fazia o
                // preview repintar o mesmo quadro 12x antes de trocar de imagem.
                bool faceReadOk = false;
                bool faceFrameIsNew = false;
                Mat? faceFrame = null;
                bool faceFrameNeededNow = _pendingLearnFace
                    || string.Equals(_pendingManualTrigger, "Manual c/ Rosto", StringComparison.OrdinalIgnoreCase);

                if (_config.FaceCameraIndex == _config.CameraIndex)
                {
                    // Mesma webcam nos dois papéis: o frame da principal já é
                    // o do rosto, sem passar pelo pump.
                    frame.CopyTo(FaceFrameBuffer);
                    faceFrame = FaceFrameBuffer;
                    faceReadOk = !FaceFrameBuffer.Empty();
                    faceFrameIsNew = faceReadOk;
                }
                else
                {
                    // Um passo atrás do pump: preview, detecção e composição
                    // recebem sempre o quadro mais recente disponível.
                    faceReadOk = TryTakeLatestFaceFrame(FaceFrameBuffer, faceFrameNeededNow,
                        out long seq);
                    if (faceReadOk)
                    {
                        faceFrame = FaceFrameBuffer;
                        // Reconhecer o mesmo quadro duas vezes não só gasta
                        // CPU: reinicia o relógio de "a mesma pessoa está aqui há
                        // N s" e pode gravar a mesma pose duas vezes na galeria.
                        faceFrameIsNew = seq != _faceFrameSeen;
                        _faceFrameSeen = seq;
                    }
                }
                swRead.Stop();
                Accumulate(ref _readMs, swRead.ElapsedMilliseconds);

                if (_pendingLearnFace)
                {
                    _pendingLearnFace = false;
                    if (faceReadOk && faceFrame != null)
                    {
                        ProcessLearnCurrentFace(faceFrame);
                    }
                    else
                    {
                        LearnFaceCompleted?.Invoke(false, "Câmera de rosto indisponível");
                    }
                }

                var swDetect = Stopwatch.StartNew();
                bool fire = false;
                Rect[] faceBoxes = _lastFaceBoxes;
                Point2f[][] faceLandmarks = _lastFaceLandmarks;
                Mat? captureFaceFrame = faceFrame;
                string trigger = _pendingManualTrigger;
                
                // Disparo manual (ESPAÇO/F9/botão/bandeja) — roda detecção facial AGORA
                // para ter caixas/frame frescos na composição.
                if (!string.IsNullOrEmpty(trigger))
                {
                    _pendingManualTrigger = "";
                    bool forceFace = string.Equals(trigger, "Manual c/ Rosto", StringComparison.OrdinalIgnoreCase);
                    if (faceReadOk && faceFrame != null)
                    {
                        Detect(faceFrame, out _, out faceBoxes, out faceLandmarks);
                        captureFaceFrame = faceFrame;
                    }
                    // Fallback: usa as últimas caixas conhecidas do preview.
                    if (faceBoxes.Length == 0)
                        faceBoxes = _lastFaceBoxes;
                    // Se forçado mas sem frame atual, avisa.
                    if (forceFace && captureFaceFrame == null)
                    {
                        LoggerService.Warn("Captura forçada com rosto, mas a câmera do rosto está indisponível.");
                        StatusChanged?.Invoke("⚠ Câmera de rosto indisponível — foto sem rosto");
                    }
                    fire = true;
                }
                // Detecção facial automática (rosto confirmado). Só quando o
                // quadro do rosto é NOVO: reprocessar a mesma imagem não só
                // gasta CPU, como reinicia o relógio de "mesma pessoa há N s" e
                // pode contar a mesma pose duas vezes na galeria.
                else if (faceReadOk && faceFrameIsNew && faceFrame != null &&
                         Detect(faceFrame, out trigger, out faceBoxes, out faceLandmarks))
                {
                    fire = true;
                }
                // Intervalo programado
                else if (_config.IntervalEnabled &&
                         (DateTime.Now - _lastIntervalCapture).TotalSeconds >= _config.IntervalSeconds)
                {
                    _lastIntervalCapture = DateTime.Now;
                    trigger = "Intervalo";
                    fire = true;
                }
                // Sem câmera de rosto: só processa reconhecimento no frame principal (throttled)
                else if (!faceReadOk && _faceWatch.ElapsedMilliseconds >= _config.FaceIntervalMs)
                {
                    _faceWatch.Restart();
                    faceBoxes = Array.Empty<Rect>();
                    faceLandmarks = Array.Empty<Point2f[]>();
                    ProcessFaceRecognition(frame, faceBoxes, faceLandmarks);
                }

                // Atualiza caixas de rosto para a pré-visualização (independente de gatilhos).
                // Aqui mora a diferença entre "vídeo travado" e "vídeo com a
                // moldura parada": as caixas correm a 10 Hz, soltas do
                // FaceIntervalMs que cronometra o reconhecimento caro. Com as
                // duas atreladas, o preview repintava o mesmo quadro 12x antes
                // de trocar de imagem — era disso que vinha o "lag do rosto".
                if (faceReadOk && faceFrameIsNew && faceFrame != null &&
                    _faceBoxWatch.ElapsedMilliseconds >= FaceBoxRefreshMs)
                {
                    RefreshFaceBoxes(faceFrame);
                }
                swDetect.Stop();
                Accumulate(ref _detectMs, swDetect.ElapsedMilliseconds);

                if (fire)
                {
                    var swSave = Stopwatch.StartNew();
                    CaptureAndSave(frame, trigger, captureFaceFrame, faceBoxes);
                    swSave.Stop();
                    Accumulate(ref _saveMs, swSave.ElapsedMilliseconds);
                }

                Accumulate(ref _loops, 1);
                ReportStatsIfDue();

                // Cada preview tem o SEU relógio. Antes os dois compartilhavam
                // um só, então a câmera do rosto era publicada no máximo na
                // metade dos quadros da principal.
                if (_previewWanted && _previewWatch.ElapsedMilliseconds >= PreviewMinIntervalMs)
                {
                    _previewWatch.Restart();
                    Cv2.Resize(frame, PreviewBuffer, new Size(PreviewWidth, PreviewHeight));
                    PreviewFrame?.Invoke(PreviewBuffer);
                    Interlocked.Increment(ref _previewFrames);
                }

                // A câmera do rosto publica no RITMO DO LOOP, não no ritmo do
                // pump. Publicar só quando o quadro é novo parece mais correto,
                // mas com as duas pontas a 30 fps elas se batem: metade das
                // amostras cai no mesmo quadro e a preview desce para ~15 fps.
                // Repintar o mais recente a cada tique é o que dá movimento
                // contínuo, e a imagem nunca é velha: o pump entrega o que tem
                // de melhor e o loop mostra o que existe agora.
                if (_previewWanted && faceFrame != null && !faceFrame.Empty() &&
                    _facePreviewWatch.ElapsedMilliseconds >= PreviewMinIntervalMs)
                {
                    _facePreviewWatch.Restart();
                    Cv2.Resize(faceFrame, FacePreviewBuffer,
                        new Size(FacePreviewWidth, FacePreviewHeight));
                    DrawFaceOverlay(FacePreviewBuffer, _lastFaceBoxes, _lastFaceLabels,
                        FacePreviewWidth, FacePreviewHeight);
                    FacePreviewFrame?.Invoke(FacePreviewBuffer);
                    Interlocked.Increment(ref _previewFrames);
                }

                // --- Pacing adaptativo: só dorme o que faltar para ~30fps ---
                loopStart.Stop();
                int elapsedMs = (int)loopStart.ElapsedMilliseconds;
                int targetMs = _previewWanted ? LoopTargetMsWithPreview : LoopPauseMsWithoutPreview;
                int sleepMs = targetMs - elapsedMs;
                if (sleepMs > 0)
                    Thread.Sleep(sleepMs);
            }
            catch (Exception ex)
            {
                LoggerService.Error("Erro no loop de captura", ex);
                ErrorOccurred?.Invoke(ex.Message);
                Thread.Sleep(1000);
            }
        }
    }

    // ------------------------------------------------- Pump da câmera do rosto

    private void StartFacePump()
    {
        if (_faceThreadRunning) return;
        // Quando as duas câmeras são o mesmo dispositivo físico não há segundo
        // fluxo para bombear: o loop principal já entrega o mesmo quadro.
        if (_config.FaceCameraIndex == _config.CameraIndex) return;

        _faceThreadRunning = true;
        _faceThread = new Thread(FaceCameraLoop)
        {
            IsBackground = true,
            Name = "ScreenLab.FaceCamera",
        };
        _faceThread.Start();
    }

    /// <summary>
    /// Lê da câmera do rosto em laço próprio, sem parar. Cada Read() do MSMF
    /// bloqueia até o próximo quadro do dispositivo, então ler as duas webcams
    /// no mesmo thread custava o dobro de tempo de ciclo — as duas previews
    /// desciam para ~15 fps e a do rosto ainda chegava atrasada, porque ficava
    /// sempre atrás da leitura da principal.
    /// </summary>
    private void FaceCameraLoop()
    {
        var ct = _cts?.Token ?? new CancellationToken(true);
        var idleWatch = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested && _faceThreadRunning)
        {
            try
            {
                if (_paused)
                {
                    Thread.Sleep(200);
                    continue;
                }

                // Ritmo: com a janela visível a câmera é lida sem parar, que é
                // o que faz o preview andar a 30 fps. Oculto na bandeja não há
                // preview para alimentar — mas o RECONHECIMENTO continua
                // rodando (é ele que troca o operador), então o pump respira no
                // mesmo compasso do FaceIntervalMs em vez de girar à toa.
                bool highRate = _previewWanted || _pendingLearnFace
                    || !string.IsNullOrEmpty(_pendingEnrollmentUser)
                    || !string.IsNullOrEmpty(_pendingManualTrigger);
                if (!highRate && idleWatch.ElapsedMilliseconds < Math.Max(150, _config.FaceIntervalMs))
                {
                    Thread.Sleep(25);
                    continue;
                }
                idleWatch.Restart();

                if (!EnsureFaceCameraOpen())
                {
                    Thread.Sleep(200);
                    continue;
                }

                var capture = _faceCapture;
                if (capture == null)
                {
                    Thread.Sleep(100);
                    continue;
                }

                var back = _faceBack;
                if (back == null)
                {
                    back = new Mat();
                    _faceBack = back;
                }

                if (!capture.Read(back) || back.Empty())
                {
                    // Câmera do rosto desconectada/travada. Read() que falha
                    // no MSMF costuma deixar o source em estado ruim: derrubar
                    // e reabrir é o que faz ela voltar sozinha.
                    TryDisposeFaceCapture();
                    Thread.Sleep(300);
                    continue;
                }

                // Troca front/back sob o lock. O consumidor copia de _faceFront
                // também sob o lock, então nunca lê um quadro em escrita — e
                // nunca chega atrasado, porque o que importa é o ÚLTIMO.
                lock (_faceFrameLock)
                {
                    _faceBack = _faceFront;
                    _faceFront = back;
                    _faceFrameSeq++;
                }
                Interlocked.Increment(ref _faceFrames);
            }
            catch (ObjectDisposedException)
            {
                // Encerrando; o Stop() já derrubou o VideoCapture.
                break;
            }
            catch (Exception ex)
            {
                LoggerService.Error("Erro no pump da câmera do rosto", ex);
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>
    /// Copia o quadro mais recente da câmera do rosto e devolve o número de
    /// sequência dele. Com <paramref name="waitForFirst"/> maior que zero
    /// (quando o disparo depende dele agora) espera o pump entregar o primeiro
    /// quadro em vez de reportar "câmera indisponível" às cegas.
    /// </summary>
    private bool TryTakeLatestFaceFrame(Mat destination, bool waitForFirst, out long seq)
    {
        long deadline = Environment.TickCount64 + FaceFrameWaitMs;
        while (true)
        {
            lock (_faceFrameLock)
            {
                var front = _faceFront;
                if (front is not null && !front.Empty())
                {
                    front.CopyTo(destination);
                    seq = _faceFrameSeq;
                    return true;
                }
            }

            if (!waitForFirst || Environment.TickCount64 >= deadline)
            {
                seq = _faceFrameSeq;
                return false;
            }
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Atualiza só as caixas/landmarks para o preview, sem reconhecimento.
    /// Deixado separado do <see cref="Detect"/> porque o rótulo que o
    /// reconhecimento produz (o nome da pessoa) é caro e não precisa correr a
    /// 10 Hz — mas o quadrado verde precisa, senão o vídeo anda e a moldura
    /// não, e o usuário lê isso como travamento.
    /// </summary>
    private void RefreshFaceBoxes(Mat frame)
    {
        bool detectFaces = _yunet != null || _faceCascade != null || _profileFaceCascade != null;
        if (!detectFaces)
        {
            _lastFaceBoxes = Array.Empty<Rect>();
            _lastFaceLandmarks = Array.Empty<Point2f[]>();
            _lastFaceLabels = Array.Empty<string>();
            return;
        }

        _faceBoxWatch.Restart();
        (Rect[] boxes, Point2f[][] landmarks) = DetectFacesInFrame(frame);
        _lastFaceBoxes = boxes;
        _lastFaceLandmarks = landmarks;

        // Os rótulos são propriedade do reconhecimento. Só crescem/encolhem
        // junto com as caixas; sobrescrever aqui apagaria o nome da pessoa 10x
        // por segundo, que era parte da leitura de "lag".
        var labels = new string[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
            labels[i] = i < _lastFaceLabels.Length ? _lastFaceLabels[i] : "RECONHECENDO";
        _lastFaceLabels = labels;
    }

    private bool EnsureCameraOpen()
    {
        var capture = _capture;
        if (capture != null && capture.IsOpened())
        {
            _cameraFailStreak = 0;
            return true;
        }

        // Troca de índice pedida de fora: o descarte acontece AQUI, na thread
        // que ia ler, e só agora que nenhum Read() está em voo.
        if (_captureNeedsReopen)
        {
            _captureNeedsReopen = false;
            TryDisposeCapture();
        }

        // Try MSMF first (Media Foundation) - delivers 1080p@30fps like Windows Camera
        // Fall back to DSHOW if MSMF fails
        var apis = new[] { VideoCaptureAPIs.MSMF, VideoCaptureAPIs.DSHOW };

        foreach (var api in apis)
        {
            try
            {
                var cap = new VideoCapture(_config.CameraIndex, api);
                if (!cap.IsOpened())
                {
                    cap.Dispose();
                    continue;
                }

                // MSMF entrega 1080p@30 como o app Câmera do Windows; deixamos
                // o formato nativo e só negociamos taxa/resolução.
                _capture = cap;
                _cameraFailStreak = 0;
                int fps = NegotiateFps(cap, api, _config.CameraIndex, "principal",
                    _config.VideoWidth, _config.VideoHeight);
                LoggerService.Info(
                    $"Câmera {_config.CameraIndex} aberta via {api} em {cap.FrameWidth}x{cap.FrameHeight}@{fps}fps");
                StatusChanged?.Invoke($"Câmera {_config.CameraIndex}: {cap.FrameWidth}x{cap.FrameHeight}@{fps}fps");
                return true;
            }
            catch (Exception ex)
            {
                LoggerService.Warn($"Falha ao abrir câmera {_config.CameraIndex} via {api}: {ex.Message}");
                // Try next API
            }
        }

        _cameraFailStreak++;
        LogCameraUnavailable();
        return false;
    }

    /// <summary>
    /// Aplica o formato pedido e devolve o FPS realmente negociado.
    /// A ordem importa: no MSMF a resolução é a troca de modo, e pedir o
    /// frameRate depois dela é silenciosamente ignorado — o device fica no
    /// modo antigo e o preview despenca. Por isso frameRate vem PRIMEIRO.
    /// </summary>
    private static int NegotiateFps(VideoCapture cap, VideoCaptureAPIs api, int index,
        string role, int requestedWidth, int requestedHeight)
    {
        // DSHOW só entrega taxa decente em MJPG: em YUY2 uma 1080p passa de
        // 30 para ~5 fps, e é o modo padrão da maioria das webcams.
        if (api == VideoCaptureAPIs.DSHOW)
            cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));

        cap.Set(VideoCaptureProperties.Fps, TargetCameraFps);

        // A resolução é o que realmente troca o modo do device. Vem por último
        // e por eixo: muitos drivers re-resolvem a cada Set, e pedir largura e
        // altura de uma vez faz o segundo ser recusado.
        if (requestedWidth > 0)
            cap.Set(VideoCaptureProperties.FrameWidth, requestedWidth);
        if (requestedHeight > 0)
            cap.Set(VideoCaptureProperties.FrameHeight, requestedHeight);

        int fps = (int)Math.Round(cap.Get(VideoCaptureProperties.Fps));
        if (fps < MinAcceptableCameraFps)
        {
            // Não é erro fatal, mas explica "a câmera não responde bem":
            // o driver entregou menos que 20 fps e nenhum ajuste do app
            // conserta isso — só trocar a câmera ou reduzir a resolução.
            LoggerService.Warn(
                $"Câmera {role} ({index}) negociou apenas {fps} fps via {api} "
                + $"(pedido {TargetCameraFps}); preview pode parecer lento.");
        }
        // Tolerância de 16 px: o MSMF às vezes arredonda o modo para um
        // múltiplo de 16 (620 -> 640) sem que isso signifique falha. Só uma
        // diferença de verdade interessa, senão o aviso vira ruído no log.
        const int widthTolerancePx = 16;
        if (requestedWidth > 0 && Math.Abs((int)cap.FrameWidth - requestedWidth) > widthTolerancePx)
        {
            LoggerService.Warn(
                $"Câmera {role} ({index}) ignorou a resolução pedida "
                + $"({requestedWidth}x{requestedHeight}); usando {cap.FrameWidth}x{cap.FrameHeight}.");
        }
        return fps;
    }

    private bool EnsureFaceCameraOpen()
    {
        if (_config.FaceCameraIndex == _config.CameraIndex)
            return _capture?.IsOpened() == true;

        var capture = _faceCapture;
        if (capture != null && capture.IsOpened())
            return true;
        if (DateTime.Now < _nextFaceCameraRetryAt)
            return false;

        // Mesma ideia da câmera principal: quem descarta é a thread do pump,
        // que é quem está lendo — nunca a thread de UI, que pode chamar isso
        // com um Read() em voo.
        if (_faceCaptureNeedsReopen)
        {
            _faceCaptureNeedsReopen = false;
            TryDisposeFaceCapture();
        }


        // Try MSMF first (Media Foundation) — better fps like Windows Camera
        // Fall back to DSHOW if MSMF fails
        var apis = new[] { VideoCaptureAPIs.MSMF, VideoCaptureAPIs.DSHOW };

        foreach (var api in apis)
        {
            try
            {
                var cap = new VideoCapture(_config.FaceCameraIndex, api);
                if (!cap.IsOpened())
                {
                    cap.Dispose();
                    continue;
                }

                // MSMF entrega o formato nativo; negociamos taxa e resolução
                // na ordem certa (ver NegotiateFps).
                _faceCapture = cap;
                _faceCameraFailStreak = 0;
                _nextFaceCameraRetryAt = DateTime.MinValue;
                int fps = NegotiateFps(cap, api, _config.FaceCameraIndex, "rosto",
                    _config.FaceVideoWidth, _config.FaceVideoHeight);
                LoggerService.Info(
                    $"Câmera do rosto {_config.FaceCameraIndex} aberta via {api} em {cap.FrameWidth}x{cap.FrameHeight}@{fps}fps");
                StatusChanged?.Invoke(
                    $"Câmera do rosto {_config.FaceCameraIndex}: {cap.FrameWidth}x{cap.FrameHeight}@{fps}fps");
                return true;
            }
            catch (Exception ex)
            {
                LoggerService.Warn($"Falha ao abrir câmera do rosto {_config.FaceCameraIndex} via {api}: {ex.Message}");
                // Try next API
            }
        }

        ScheduleFaceRetry();
        LogFaceCameraUnavailable();
        return false;
    }

    /// <summary>Backoff progressivo: tenta logo no início, depois espaça para
    /// não ficar "se perguntando" pela câmera que não está ligada.</summary>
    private void ScheduleFaceRetry()
    {
        int delay = _faceCameraFailStreak < 3 ? 2 : _faceCameraFailStreak < 8 ? 10 : 60;
        _faceCameraFailStreak++;
        _nextFaceCameraRetryAt = DateTime.Now.AddSeconds(delay);
    }

    private void LogFaceCameraUnavailable()
    {
        if ((DateTime.Now - _lastFaceCameraFailLog).TotalSeconds < 30)
            return;
        _lastFaceCameraFailLog = DateTime.Now;
        LoggerService.Warn(
            $"Câmera do rosto {_config.FaceCameraIndex} indisponível — tentando novamente");
        StatusChanged?.Invoke(
            $"Câmera do rosto {_config.FaceCameraIndex} indisponível; foto da peça continua normalmente");
    }

    private void LogCameraUnavailable()
    {
        // Evita inundar o log: registra no máximo 1 aviso a cada 30 s.
        if ((DateTime.Now - _lastCameraFailLog).TotalSeconds < 30)
            return;
        _lastCameraFailLog = DateTime.Now;
        LoggerService.Warn($"Câmera {_config.CameraIndex} indisponível — tentando novamente a cada 2 s");
        StatusChanged?.Invoke($"Câmera {_config.CameraIndex} indisponível (tentando novamente...)");
    }

    private void TryDisposeCapture()
    {
        VideoCapture? capture;
        lock (_stateLock)
        {
            capture = _capture;
            _capture = null;
        }
        capture?.Dispose();
    }

    private void TryDisposeFaceCapture()
    {
        VideoCapture? capture;
        lock (_stateLock)
        {
            capture = _faceCapture;
            _faceCapture = null;
            _nextFaceCameraRetryAt = DateTime.Now.AddSeconds(2);
        }
        capture?.Dispose();
    }

    /// <summary>Reduz o quadro e roda o detector. Usado tanto pelo caminho caro
    /// (reconhecimento) quanto pelo rápido (só as caixas do preview).</summary>
    private (Rect[] Faces, Point2f[][] Landmarks) DetectFacesInFrame(Mat frame)
    {
        using var small = new Mat();
        using var gray = new Mat();
        Cv2.Resize(frame, small, new Size(DetectionWidth, DetectionHeight));
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new Size(21, 21), 0);
        return DetectFaces(small, gray);
    }

    /// <summary>Detecção facial + reconhecimento sobre o quadro da câmera do rosto.
    /// Atualiza caixas/landmarks sempre; retorna true só se gatilho de captura (rosto confirmado).</summary>
    private bool Detect(Mat frame, out string trigger, out Rect[] faceBoxes, out Point2f[][] faceLandmarks)
    {
        trigger = "";
        bool detectFaces = _yunet != null || _faceCascade != null || _profileFaceCascade != null;
        if (!detectFaces || _faceWatch.ElapsedMilliseconds < _config.FaceIntervalMs)
        {
            faceBoxes = _lastFaceBoxes;
            faceLandmarks = _lastFaceLandmarks;
            return false;
        }

        _faceWatch.Restart();
        (faceBoxes, faceLandmarks) = DetectFacesInFrame(frame);
        _lastFaceBoxes = faceBoxes;      // Sempre atualiza para preview
        _lastFaceLandmarks = faceLandmarks;
        if (ProcessFaceRecognition(frame, faceBoxes, faceLandmarks))
        {
            trigger = "Rosto";
            return true;
        }
        return false;
    }

    /// <summary>Apenas detecta faces (sem reconhecimento/gatilho) para manter preview vivo.</summary>
    private void DetectFacesForPreview(Mat frame)
    {
        bool detectFaces = _yunet != null || _faceCascade != null || _profileFaceCascade != null;
        if (!detectFaces)
        {
            _lastFaceBoxes = Array.Empty<Rect>();
            _lastFaceLandmarks = Array.Empty<Point2f[]>();
            _lastFaceLabels = Array.Empty<string>();
            return;
        }

        var (boxes, landmarks) = DetectFacesInFrame(frame);
        _lastFaceBoxes = boxes;
        _lastFaceLandmarks = landmarks;
        _lastFaceLabels = new string[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
            _lastFaceLabels[i] = "RECONHECENDO";
    }

    private (Rect[] Faces, Point2f[][] Landmarks) DetectFaces(Mat small, Mat gray)
    {
        if (_yunet != null)
        {
            // O YuNet só aceita entrada quadrada. Esticar 320x240 direto para
            // 320x320 achata o rosto: a caixa saía com proporção ~0,55 em vez
            // de ~0,8, e os landmarks saem deformados junto. Encolher
            // preservando a proporção e centralizar com borda preta (o que o
            // FaceDetectorYN oficial faz) mantém a geometria fiel.
            float scale = Math.Min(YuNetInput / (float)small.Width,
                                   YuNetInput / (float)small.Height);
            int netWidth = (int)Math.Round(small.Width * scale);
            int netHeight = (int)Math.Round(small.Height * scale);
            int padX = (YuNetInput - netWidth) / 2;
            int padY = (YuNetInput - netHeight) / 2;

            using var resized = new Mat();
            Cv2.Resize(small, resized, new Size(netWidth, netHeight));
            using var canvas = new Mat(YuNetInput, YuNetInput, MatType.CV_8UC3, Scalar.All(0));
            using (var slot = new Mat(canvas, new Rect(padX, padY, netWidth, netHeight)))
                resized.CopyTo(slot);

            using var blob = CvDnn.BlobFromImage(canvas, 1.0,
                new Size(YuNetInput, YuNetInput), new Scalar(), false, false);
            _yunet.SetInput(blob);
            string[] outputNames =
            {
                "cls_8", "cls_16", "cls_32", "obj_8", "obj_16", "obj_32",
                "bbox_8", "bbox_16", "bbox_32", "kps_8", "kps_16", "kps_32",
            };
            var outputBlobs = new List<Mat>(outputNames.Length);
            for (int i = 0; i < outputNames.Length; i++)
                outputBlobs.Add(new Mat());
            try
            {
                _yunet.Forward(outputBlobs, outputNames);
                bool invalidOutputs = outputBlobs.Count < 12;
                foreach (var output in outputBlobs)
                    invalidOutputs |= output.Empty();
                if (invalidOutputs)
                    return DetectHaarFaces(gray);

                // Rede -> quadro de detecção: desfaz o letterbox.
                float MapX(float netX) => (netX - padX) / scale;
                float MapY(float netY) => (netY - padY) / scale;

                var hits = new List<FaceHit>();
                int[] strides = { 8, 16, 32 };
                for (int s = 0; s < strides.Length; s++)
                {
                    int stride = strides[s];
                    int gridRows = YuNetInput / stride;
                    int gridCols = YuNetInput / stride;
                    using var cls = outputBlobs[s].Reshape(1, gridRows * gridCols);
                    using var obj = outputBlobs[s + 3].Reshape(1, gridRows * gridCols);
                    using var bbox = outputBlobs[s + 6].Reshape(1, gridRows * gridCols);
                    using var kps = outputBlobs[s + 9].Reshape(1, gridRows * gridCols);

                    for (int row = 0; row < gridRows; row++)
                    {
                        for (int col = 0; col < gridCols; col++)
                        {
                            int index = row * gridCols + col;
                            // cls e obj já vêm como sigmoid; a média geométrica
                            // exige que as DUAS concordem que há um rosto ali.
                            float score = MathF.Sqrt(Math.Clamp(cls.At<float>(index, 0), 0, 1) *
                                Math.Clamp(obj.At<float>(index, 0), 0, 1));
                            if (score < YuNetMinScore)
                                continue;

                            float centerX = (col + bbox.At<float>(index, 0)) * stride;
                            float centerY = (row + bbox.At<float>(index, 1)) * stride;
                            float width = MathF.Exp(bbox.At<float>(index, 2)) * stride;
                            float height = MathF.Exp(bbox.At<float>(index, 3)) * stride;
                            int x = Math.Clamp((int)MathF.Round(MapX(centerX - width / 2)),
                                0, DetectionWidth - 1);
                            int y = Math.Clamp((int)MathF.Round(MapY(centerY - height / 2)),
                                0, DetectionHeight - 1);
                            int right = Math.Clamp((int)MathF.Round(MapX(centerX + width / 2)),
                                x + 1, DetectionWidth);
                            int bottom = Math.Clamp((int)MathF.Round(MapY(centerY + height / 2)),
                                y + 1, DetectionHeight);
                            if (right - x < 8 || bottom - y < 8)
                                continue;

                            var points = new Point2f[5];
                            for (int point = 0; point < 5; point++)
                            {
                                points[point] = new Point2f(
                                    MapX((kps.At<float>(index, point * 2) + col) * stride),
                                    MapY((kps.At<float>(index, point * 2 + 1) + row) * stride));
                            }
                            hits.Add(new FaceHit(new Rect(x, y, right - x, bottom - y), points, score));
                        }
                    }
                }

                var kept = SuppressOverlappingFaces(hits);
                // YuNet é o detector principal, mas em imagens muito escuras
                // ou muito inclinadas ele pode perder todos os candidatos. O
                // fallback só é consultado quando não há nenhum, evitando
                // duplicar/causar caixas extras no caso normal.
                if (kept.Count == 0)
                    return DetectHaarFaces(gray);

                var boxes = new Rect[kept.Count];
                var allLandmarks = new Point2f[kept.Count][];
                for (int i = 0; i < kept.Count; i++)
                {
                    boxes[i] = kept[i].Box;
                    allLandmarks[i] = kept[i].Landmarks ?? new Point2f[0];
                }
                return (boxes, allLandmarks);
            }
            catch (Exception ex)
            {
                if (!_yunetInferenceWarningLogged)
                {
                    _yunetInferenceWarningLogged = true;
                    LoggerService.Warn($"YuNet falhou durante a inferência; usando fallback Haar: {ex.Message}");
                }
                return DetectHaarFaces(gray);
            }
            finally
            {
                foreach (var output in outputBlobs)
                    output.Dispose();
            }
        }

        return DetectHaarFaces(gray);
    }

    /// <summary>
    /// Quanto a cabeça está virada, em graus (0 = de frente). Mede a assimetria
    /// das distâncias nariz-olho: de frente as duas são iguais; virado, uma
    /// cresce e a outra encurta. Serve para o app desconfiar do embedding
    /// quando o rosto está de perfil — é quando a galeria de uma pessoa passa
    /// a parecer com a de outra, porque o modelo tem menos informação.
    /// </summary>
    private static double EstimateHeadYaw(Point2f[]? landmarks)
    {
        if (landmarks is not { Length: >= 5 }) return 0;
        double dRight = Distance(landmarks[0], landmarks[2]);
        double dLeft = Distance(landmarks[1], landmarks[2]);
        double soma = dRight + dLeft;
        if (soma < 1e-6) return 0;
        double k = (dRight - dLeft) / soma;   // -1..1, 0 = perfeitamente de frente
        return Math.Asin(Math.Clamp(k, -1, 1)) * 180.0 / Math.PI;
    }

    private static double Distance(Point2f a, Point2f b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private DateTime _lastRankLogAt = DateTime.MinValue;
    private static readonly TimeSpan RankLogInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Registra periodicamente como o rosto da frente está se comparando com
    /// cada galeria. Sem isso, "ele chamou eu de Ailton" não tem como ser
    /// diagnosticado: o log antigo só guardava o vencedor.
    /// </summary>
    private void LogRecognitionRanking(string user, float[] embedding, double yaw)
    {
        DateTime now = DateTime.Now;
        if (now - _lastRankLogAt < RankLogInterval) return;
        _lastRankLogAt = now;

        var ranking = _faceRecognizer.RankAll(embedding);
        if (ranking.Count == 0) return;

        float limiar = _faceRecognizer.MinimumCosine;
        float margemMin = _faceRecognizer.MinimumMargin;
        string todos = string.Join(", ", ranking.Select(r => $"{r.Name}={r.Cosine:F3}"));

        float melhor = ranking[0].Cosine;
        float segundo = ranking.Count > 1 ? ranking[1].Cosine : 0f;
        float margem = melhor - segundo;
        bool acimaDoCorte = melhor >= limiar;
        bool margemOk = margem >= margemMin;

        LoggerService.Info(
            $"[Rank] perfil={yaw:F0}° Escolido={user} | {todos} | "
            + $"limiar={limiar:F3} acima={(acimaDoCorte ? "sim" : "NAO")} "
            + $"margem={margem:F3} {(margemOk ? "ok" : "PEQUENA")} "
            + $"| {(acimaDoCorte && margemOk ? "reconhecido" : "descartado")}");
    }

    private readonly record struct FaceHit(Rect Box, Point2f[]? Landmarks, float Score);

    private static (Rect[] Faces, Point2f[][] Landmarks) DetectHaarFaces(Mat gray)
    {
        var haarFaces = new List<Rect>();
        if (_faceCascade != null)
        {
            haarFaces.AddRange(_faceCascade.DetectMultiScale(
                gray, 1.1, 4, HaarDetectionTypes.ScaleImage, new Size(50, 50)));
        }

        if (_profileFaceCascade != null)
        {
            haarFaces.AddRange(_profileFaceCascade.DetectMultiScale(
                gray, 1.1, 4, HaarDetectionTypes.ScaleImage, new Size(50, 50)));

            using var mirrored = new Mat();
            Cv2.Flip(gray, mirrored, FlipMode.Y);
            foreach (var face in _profileFaceCascade.DetectMultiScale(
                         mirrored, 1.1, 4, HaarDetectionTypes.ScaleImage, new Size(50, 50)))
            {
                haarFaces.Add(new Rect(DetectionWidth - face.X - face.Width, face.Y, face.Width, face.Height));
            }
        }

        haarFaces.Sort((left, right) => (right.Width * right.Height).CompareTo(left.Width * left.Height));
        var distinctHaar = new List<Rect>();
        foreach (var face in haarFaces)
        {
            bool duplicate = false;
            foreach (var existing in distinctHaar)
            {
                if (IntersectionOverUnion(face, existing) >= 0.35)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
                distinctHaar.Add(face);
        }
        return (distinctHaar.ToArray(), new Point2f[distinctHaar.Count][]);
    }

    private static List<FaceHit> SuppressOverlappingFaces(List<FaceHit> hits)
    {
        var kept = new List<FaceHit>(hits.Count);
        foreach (var hit in hits.OrderByDescending(h => h.Score))
        {
            bool duplicate = false;
            foreach (var existing in kept)
            {
                if (IntersectionOverUnion(hit.Box, existing.Box) >= FaceNmsIou)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
                kept.Add(hit);
        }
        return kept;
    }

    /// <returns>True somente quando todos os gates liberaram uma foto.</returns>
    private bool ProcessFaceRecognition(Mat frame, Rect[] faces, Point2f[][] landmarks)
    {
        _lastFaceLabels = new string[faces.Length];
        DateTime now = DateTime.Now;

        // A manutenção da sessão de cadastro roda ANTES de qualquer saída
        // antecipada. Se ficasse depois, um cadastro aberto com a pessoa longe
        // da câmera nunca veria o timeout e a interface ficaria travada.
        string enrollmentUser = _pendingEnrollmentUser;
        if (_pendingEnrollmentReset)
            ResetEnrollmentSession();
        if (!string.IsNullOrEmpty(enrollmentUser) && _enrollmentStartedAt != DateTime.MinValue &&
            (now - _enrollmentStartedAt).TotalSeconds > EnrollmentTimeoutSeconds)
        {
            CancelFaceEnrollment(
                $"O cadastro ficou {EnrollmentTimeoutSeconds / 60} minutos sem receber poses e foi encerrado.");
            enrollmentUser = "";
        }

        if (faces.Length == 0)
        {
            ResetTrackedIdentity();
            ResetFaceDwell();
            return false;
        }

        if (!string.IsNullOrEmpty(enrollmentUser))
        {
            if (_enrollmentStartedAt == DateTime.MinValue)
                _enrollmentStartedAt = now;

            ResetTrackedIdentity();
            ResetFaceDwell();
            ProcessFaceEnrollment(frame, faces, landmarks, enrollmentUser, now);
            return false;
        }

        // Com mais de uma pessoa no quadro não existe identidade individual
        // confiável. Mostramos os rótulos, mas nunca mudamos o usuário nem
        // capturamos: isso evita atribuir a foto de uma pessoa ao operador da outra.
        if (faces.Length > 1)
        {
            ResetTrackedIdentity();
            ResetFaceDwell();
            for (int i = 0; i < faces.Length; i++)
                DescribeFaceWithoutTracking(frame, faces[i],
                    i < landmarks.Length ? landmarks[i] : null, i);
            return false;
        }

        Rect faceRect = ScaleFaceRect(faces[0], frame.Width, frame.Height);
        if (!FaceQualityOk(frame, faceRect, out string qualityReason))
        {
            _lastFaceLabels[0] = qualityReason;
            ResetTrackedIdentity();
            ResetFaceDwell();
            return false;
        }

        // A opção sem exigeência é explícita: nesse modo a pessoa continua
        // usando o operador selecionado, mas ainda passa por tamanho, foco,
        // brilho, permanência e cooldown. É útil quando a galeria está vazia.
        if (!_config.FaceCaptureRequireKnown)
        {
            string activeUser = _users.ActiveUser;
            _lastFaceLabels[0] = $"{activeUser} (sem confirmar)";
            return UpdateFaceCaptureGate(activeUser, now);
        }

        using var crop = new Mat(frame, faceRect);
        Point2f[]? cropLandmarks = LandmarksToCrop(
            landmarks.Length > 0 ? landmarks[0] : null, faceRect, frame.Width, frame.Height);
        if (!TryAlignFace(crop, cropLandmarks, out Mat aligned))
        {
            _lastFaceLabels[0] = "ALINHAMENTO FALHOU";
            ResetTrackedIdentity();
            ResetFaceDwell();
            return false;
        }

        using (aligned)
        {
            float[]? embedding = _faceRecognizer.EmbedFace(aligned);
            var match = embedding == null ? null : _faceRecognizer.RecognizeEmbedding(embedding);
            if (embedding == null)
            {
                _lastFaceLabels[0] = "MODELO INDISPONIVEL";
                ResetTrackedIdentity();
                ResetFaceDwell();
                return false;
            }

            // Mede o quanto está de perfil e registra a comparação completa,
            // mesmo quando o frame é descartado. É o que permite entender
            // depois por que o app escolheu um nome e não o outro.
            double yaw = EstimateHeadYaw(cropLandmarks);

            if (match is not { } result)
            {
                LogRecognitionRanking("ninguem", embedding, yaw);
                _lastFaceLabels[0] = yaw > 25
                    ? $"DESCONHECIDO (perfil {yaw:F0}°)"
                    : "DESCONHECIDO";
                ResetTrackedIdentity();
                ResetFaceDwell();
                return false;
            }

            LogRecognitionRanking(result.Name, embedding, yaw);

            bool confirmed = UpdateIdentityState(result.Name, now, out double remainingSeconds);
            if (!confirmed)
            {
                string phase = string.IsNullOrEmpty(_confirmedUser)
                    ? "CONFIRMANDO"
                    : "AGUARDANDO TROCA";
                _lastFaceLabels[0] = $"{result.Name} {phase} {Math.Max(1, (int)Math.Ceiling(remainingSeconds))}s";
                ResetFaceDwell();
                return false;
            }

            _lastFaceLabels[0] = $"{result.Name} {result.Confidence:P0}"
                + (yaw > 25 ? $" (perfil {yaw:F0}°)" : "");

            // Aprendizado só a partir daqui, com a identidade já confirmada
            // acima. Antes, uma pose que ganhasse 3 frames seguidos por acidente
            // entrava na galeria errada e reforçava o próprio erro.
            TryAutoEnrollConfirmed(result.Name, embedding, result.Confidence);

            TryPersistRecognitionSample(result.Name, embedding, now);
            return UpdateFaceCaptureGate(result.Name, now);
        }
    }

    private void ProcessFaceEnrollment(
        Mat frame, Rect[] faces, Point2f[][] landmarks, string user, DateTime now)
    {
        if (faces.Length != 1)
        {
            _lastFaceLabels[0] = "MOSTRE 1 ROSTO";
            return;
        }

        if (_enrollmentSessionUser != user)
        {
            _enrollmentSessionUser = user;
            _enrollmentSamples.Clear();
            _lastEnrollmentPrompt = "";
            FaceEnrollmentProgress?.Invoke(user, 0, EnrollmentSampleCount, EnrollmentPrompts[0], 0);
        }

        Rect faceRect = ScaleFaceRect(faces[0], frame.Width, frame.Height);
        if (!FaceQualityOk(frame, faceRect, out string qualityReason))
        {
            _lastFaceLabels[0] = qualityReason;
            return;
        }

        int sampleIndex = _enrollmentSamples.Count;
        string prompt = EnrollmentPrompts[Math.Min(sampleIndex, EnrollmentPrompts.Length - 1)];
        if (!_pendingEnrollmentCapture)
        {
            _lastFaceLabels[0] = $"{prompt} - CLIQUE CAPTURAR";
            if (_lastEnrollmentPrompt != prompt)
            {
                _lastEnrollmentPrompt = prompt;
                FaceEnrollmentProgress?.Invoke(user, sampleIndex, EnrollmentSampleCount, prompt, 0);
            }
            return;
        }
        _pendingEnrollmentCapture = false;

        using var crop = new Mat(frame, faceRect);
        Point2f[]? cropLandmarks = LandmarksToCrop(
            landmarks.Length > 0 ? landmarks[0] : null, faceRect, frame.Width, frame.Height);
        if (!TryAlignFace(crop, cropLandmarks, out Mat aligned))
        {
            _lastFaceLabels[0] = "ALINHAMENTO FALHOU";
            return;
        }

        using (aligned)
        {
            float[]? embedding = _faceRecognizer.EmbedFace(aligned);
            if (embedding == null)
            {
                _lastFaceLabels[0] = "MODELO INDISPONIVEL";
                return;
            }

            // Cinco quadros da mesma pose não melhoram a galeria. Pedimos uma
            // pose realmente diferente antes de avançar, tornando o cadastro
            // menos frágil a luz, distância e pequeno desalinhamento.
            if (_enrollmentSamples.Any(sample => CosineSimilarity(sample, embedding) >=
                    EnrollmentDuplicateSimilarity))
            {
                _lastEnrollmentPrompt = "";
                _lastFaceLabels[0] = "VARIE MAIS A POSE";
                FaceEnrollmentProgress?.Invoke(user, sampleIndex, EnrollmentSampleCount, "VARIE MAIS A POSE", 0);
                return;
            }

            _enrollmentSamples.Add(embedding);
            _lastFaceLabels[0] = $"CADASTRANDO {_enrollmentSamples.Count}/{EnrollmentSampleCount}";
            FaceEnrollmentProgress?.Invoke(
                user, _enrollmentSamples.Count, EnrollmentSampleCount, "OK", 0);
            if (_enrollmentSamples.Count < EnrollmentSampleCount)
            {
                _lastEnrollmentPrompt = "";
                return;
            }

            if (!_users.SaveFaceTemplates(user, new List<float[]>(_enrollmentSamples)))
            {
                _lastFaceLabels[0] = "FALHA AO SALVAR CADASTRO";
                // Sem isso a UI ficaria esperando um evento de conclusão que
                // nunca viria, com o botão travado em "Concluindo...".
                CancelFaceEnrollment("Não foi possível gravar o cadastro em disco.");
                return;
            }
            _pendingEnrollmentUser = "";
            _pendingEnrollmentCapture = false;
            ResetEnrollmentSession();
            _faceRecognizer.ResetGallery(_users.FaceTemplateSetsSnapshot());
            _lastFaceLabels[0] = "CADASTRADO: " + user;
            FaceEnrollmentCompleted?.Invoke(user);
        }
    }

    private void DescribeFaceWithoutTracking(
        Mat frame, Rect detectedFace, Point2f[]? detectedLandmarks, int labelIndex)
    {
        Rect faceRect = ScaleFaceRect(detectedFace, frame.Width, frame.Height);
        if (!FaceQualityOk(frame, faceRect, out string qualityReason))
        {
            _lastFaceLabels[labelIndex] = qualityReason;
            return;
        }

        using var crop = new Mat(frame, faceRect);
        Point2f[]? cropLandmarks = LandmarksToCrop(
            detectedLandmarks, faceRect, frame.Width, frame.Height);
        if (!TryAlignFace(crop, cropLandmarks, out Mat aligned))
        {
            _lastFaceLabels[labelIndex] = "ALINHAMENTO FALHOU";
            return;
        }

        using (aligned)
        {
            float[]? embedding = _faceRecognizer.EmbedFace(aligned);
            var match = embedding == null ? null : _faceRecognizer.RecognizeEmbedding(embedding);
            _lastFaceLabels[labelIndex] = embedding == null
                ? "MODELO INDISPONIVEL"
                : match is { } result ? $"{result.Name} {result.Confidence:P0}" : "DESCONHECIDO";
        }
    }

    private bool UpdateIdentityState(string user, DateTime now, out double remainingSeconds)
    {
        remainingSeconds = 0;
        if (!string.Equals(_trackedUser, user, StringComparison.OrdinalIgnoreCase))
        {
            _trackedUser = user;
            _trackedSince = now;
            _lastSampleAt = DateTime.MinValue;
            ResetFaceDwell();
        }

        double confirmSeconds = Math.Max(0, _config.FaceConfirmSeconds);
        double heldSeconds = Math.Max(0, (now - _trackedSince).TotalSeconds);
        if (heldSeconds < confirmSeconds)
        {
            remainingSeconds = confirmSeconds - heldSeconds;
            return false;
        }

        if (string.Equals(_confirmedUser, user, StringComparison.OrdinalIgnoreCase))
        {
            _lastSeenAt = now;
            return true;
        }

        if (string.IsNullOrEmpty(_confirmedUser))
        {
            ConfirmIdentity(user, now);
            return true;
        }

        // Uma pessoa já confirmada não cede o posto para um único frame. O
        // candidato também precisa ficar estável por FaceConfirmSeconds e só
        // substitui o usuário após o período de gracia sem vê-lo.
        double absentSeconds = _lastSeenAt == DateTime.MinValue
            ? double.PositiveInfinity
            : Math.Max(0, (now - _lastSeenAt).TotalSeconds);
        double graceSeconds = Math.Max(0, _config.FaceSwitchGraceSeconds);
        if (absentSeconds < graceSeconds)
        {
            remainingSeconds = graceSeconds - absentSeconds;
            return false;
        }

        ConfirmIdentity(user, now);
        return true;
    }

    private void ConfirmIdentity(string user, DateTime now)
    {
        string previous = _confirmedUser;
        _confirmedUser = user;
        _lastSeenAt = now;

        if (_autoSwitchOperator && _users.ActiveUser != user)
            _users.ActiveUser = user;
        else if (_operatorChosenByUser.Length == 0)
            _operatorChosenByUser = user;

        if (!string.Equals(previous, user, StringComparison.OrdinalIgnoreCase))
            LoggerService.Info(
                $"Identidade facial confirmada: {user} (anterior: {previous ?? "-"})"
                + (_autoSwitchOperator ? "" : " — troca automática desligada, fotos seguem o operador escolhido"));
    }

    /// <summary>
    /// Decide o aprendizado de uma pose, mas só para a identidade que o motor
    /// acabou de confirmar (o que exigiu <see cref="FaceConfirmSeconds"/> de
    /// reconhecimento estável). É a barreira contra o loop de reforço: uma pose
    /// que ganhasse a corrida por 3 frames seguidos não entra em nada.
    /// </summary>
    private void TryAutoEnrollConfirmed(string user, float[] embedding, float confidence)
    {
        if (embedding is not { Length: > 0 }) return;
        if (!string.Equals(user, _confirmedUser, StringComparison.OrdinalIgnoreCase)) return;

        if (_faceRecognizer.EvaluateAutoEnroll(user, embedding, confidence) !=
            FaceRecognizerService.AutoEnrollDecision.Accepted)
        {
            return;
        }

        // O buffer é do frame atual e pode ser reaproveitado: a fila recebe
        // uma cópia própria.
        EnqueueEmbeddingWrite(user, (float[])embedding.Clone());
    }

    private void TryPersistRecognitionSample(string user, float[] embedding, DateTime now)
    {
        if (_lastSampleAt != DateTime.MinValue && now - _lastSampleAt < SampleInterval)
            return;

        _lastSampleAt = now;

        // Só enfileira. A escrita (memória + disco) acontece na thread dedicada,
        // e o CommitTemplate dela descarta duplicata e usuário cheio sem tocar
        // no arquivo. Aqui, escrever direto reescrevia o config.json a cada
        // SampleInterval, para sempre, mesmo com a galeria já completa.
        EnqueueEmbeddingWrite(user, embedding);
    }

    private bool UpdateFaceCaptureGate(string user, DateTime now)
    {
        if (!_config.FaceEnabled)
        {
            // Desligar a captura facial não deve preservar um dwell antigo:
            // ao religar, a pessoa não deve receber uma foto imediata sem
            // respeitar os tempos de segurança.
            ResetFaceDwell();
            return false;
        }

        if (!string.Equals(_faceCaptureUser, user, StringComparison.OrdinalIgnoreCase))
        {
            _faceCaptureUser = user;
            _facePresentSince = now;
            return false;
        }

        if (_facePresentSince == DateTime.MinValue)
        {
            _facePresentSince = now;
            return false;
        }
        if ((now - _facePresentSince).TotalSeconds < Math.Max(0, _config.FaceCaptureDwellSeconds))
            return false;
        if (_lastCapture != DateTime.MinValue &&
            (now - _lastCapture).TotalSeconds < Math.Max(0, _config.CooldownSeconds))
            return false;
        if (_lastPhotoByUser.TryGetValue(user, out DateTime lastPhoto) &&
            (now - lastPhoto).TotalSeconds < Math.Max(0, _config.FaceCaptureCooldownSeconds))
            return false;

        // Reserva o cooldown antes de chamar a gravação. Assim uma falha de
        // disco não abre caminho para o motor tentar salvar a cada 400 ms.
        _lastPhotoByUser[user] = now;
        _facePresentSince = now;
        return true;
    }

    private void ResetTrackedIdentity()
    {
        _trackedUser = "";
        _trackedSince = DateTime.MinValue;
    }

    private void ResetFaceDwell()
    {
        _facePresentSince = DateTime.MinValue;
        _faceCaptureUser = "";
    }

    private void ResetFaceRuntimeState()
    {
        ResetTrackedIdentity();
        ResetFaceDwell();
        _confirmedUser = "";
        _lastSeenAt = DateTime.MinValue;
        _lastSampleAt = DateTime.MinValue;

        var knownUsers = _users.UserNamesSnapshot();
        foreach (string oldUser in _lastPhotoByUser.Keys.ToList())
        {
            if (!knownUsers.Contains(oldUser, StringComparer.OrdinalIgnoreCase))
                _lastPhotoByUser.Remove(oldUser);
        }
    }

    private bool FaceQualityOk(Mat frame, Rect faceRect, out string reason)
    {
        reason = "";
        double widthPercent = faceRect.Width * 100.0 / Math.Max(1, frame.Width);
        if (widthPercent < Math.Max(1, _config.FaceMinWidthPct))
        {
            reason = $"APROXIMADO ({widthPercent:0}%)";
            return false;
        }
        if (faceRect.Width < 16 || faceRect.Height < 16)
        {
            reason = "ROSTO PEQUENO";
            return false;
        }

        using var crop = new Mat(frame, faceRect);
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.MeanStdDev(gray, out Scalar brightness, out _);
        double meanBrightness = brightness[0];
        if (meanBrightness < 30)
        {
            reason = "LUZ BAIXA";
            return false;
        }
        if (meanBrightness > 225)
        {
            reason = "LUZ ALTA";
            return false;
        }

        using var laplacian = new Mat();
        Cv2.Laplacian(gray, laplacian, MatType.CV_64F, 3);
        Cv2.MeanStdDev(laplacian, out _, out Scalar standardDeviation);
        double sharpness = standardDeviation[0] * standardDeviation[0];
        if (sharpness < Math.Max(0, _config.FaceMinSharpness))
        {
            reason = "FORA DE FOCO";
            return false;
        }

        return true;
    }

    /// <summary>
    /// YuNet devolve pontos no quadro reduzido (320x240). Depois do recorte em
    /// resolução cheia, cada ponto precisa ser mapeado para o frame original e
    /// subtraído da origem da caixa. Sem essa conversão, o affine transform
    /// alinhava os olhos em posições sem relação com o rosto recortado.
    /// </summary>
    private static Point2f[]? LandmarksToCrop(
        Point2f[]? landmarks, Rect fullFace, int frameWidth, int frameHeight)
    {
        if (landmarks is not { Length: 5 })
            return null;

        double scaleX = frameWidth / (double)DetectionWidth;
        double scaleY = frameHeight / (double)DetectionHeight;
        double toleranceX = Math.Max(3, fullFace.Width * 0.03);
        double toleranceY = Math.Max(3, fullFace.Height * 0.03);
        var converted = new Point2f[5];
        for (int i = 0; i < converted.Length; i++)
        {
            double fullX = landmarks[i].X * scaleX;
            double fullY = landmarks[i].Y * scaleY;
            if (!double.IsFinite(fullX) || !double.IsFinite(fullY) ||
                fullX < fullFace.X - toleranceX || fullX > fullFace.X + fullFace.Width + toleranceX ||
                fullY < fullFace.Y - toleranceY || fullY > fullFace.Y + fullFace.Height + toleranceY)
                return null;

            converted[i] = new Point2f((float)(fullX - fullFace.X), (float)(fullY - fullFace.Y));
        }
        return converted;
    }

    private static bool TryAlignFace(Mat face, Point2f[]? landmarks, out Mat aligned)
    {
        aligned = new Mat();
        if (face.Empty())
            return false;

        try
        {
            if (landmarks is { Length: 5 } && PlausibleEyeTriangle(landmarks, face.Width))
            {
                var source = new[] { landmarks[0], landmarks[1], landmarks[2] };
                var target = new[]
                {
                    new Point2f(38.2946f, 51.6963f),
                    new Point2f(73.5318f, 51.5014f),
                    new Point2f(56.0252f, 71.7366f),
                };
                using var transform = Cv2.GetAffineTransform(source, target);
                Cv2.WarpAffine(face, aligned, transform, new Size(112, 112),
                    InterpolationFlags.Linear, BorderTypes.Replicate);
                if (!aligned.Empty())
                    return true;
                aligned.Dispose();
                aligned = new Mat();
            }

            if (_eyeCascade == null)
                return false;

            using var gray = new Mat();
            Cv2.CvtColor(face, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.EqualizeHist(gray, gray);
            var eyes = _eyeCascade.DetectMultiScale(
                gray, 1.1, 5, HaarDetectionTypes.ScaleImage, new Size(12, 12));
            if (eyes.Length < 2)
                return false;

            Rect left = default;
            Rect right = default;
            double bestDistance = 0;
            for (int i = 0; i < eyes.Length; i++)
            {
                for (int j = i + 1; j < eyes.Length; j++)
                {
                    var first = eyes[i].X <= eyes[j].X ? eyes[i] : eyes[j];
                    var second = eyes[i].X <= eyes[j].X ? eyes[j] : eyes[i];
                    double verticalGap = Math.Abs(first.Y - second.Y);
                    double distance = second.X - first.X;
                    if (distance > bestDistance && verticalGap < distance * 0.55)
                    {
                        left = first;
                        right = second;
                        bestDistance = distance;
                    }
                }
            }

            if (bestDistance < 8)
                return false;

            var sourceLeft = new Point2f(left.X + left.Width / 2f, left.Y + left.Height / 2f);
            var sourceRight = new Point2f(right.X + right.Width / 2f, right.Y + right.Height / 2f);
            var sourceMid = new Point2f(
                (sourceLeft.X + sourceRight.X) / 2f, (sourceLeft.Y + sourceRight.Y) / 2f);
            const double targetDistance = 35.24;
            const double targetMidX = 55.91;
            const double targetMidY = 51.60;
            double angle = Math.Atan2(sourceRight.Y - sourceLeft.Y, sourceRight.X - sourceLeft.X)
                * 180.0 / Math.PI;
            double scale = targetDistance / bestDistance;

            using var eyeTransform = Cv2.GetRotationMatrix2D(sourceMid, angle, scale);
            eyeTransform.Set(0, 2, eyeTransform.At<double>(0, 2) + targetMidX - sourceMid.X);
            eyeTransform.Set(1, 2, eyeTransform.At<double>(1, 2) + targetMidY - sourceMid.Y);
            Cv2.WarpAffine(face, aligned, eyeTransform, new Size(112, 112),
                InterpolationFlags.Linear, BorderTypes.Replicate);
            return !aligned.Empty();
        }
        catch
        {
            aligned.Dispose();
            aligned = new Mat();
            return false;
        }
    }

    private static bool PlausibleEyeTriangle(Point2f[] landmarks, int faceWidth)
    {
        double eyeDistanceX = landmarks[1].X - landmarks[0].X;
        double eyeDistanceY = landmarks[1].Y - landmarks[0].Y;
        double eyeDistance = Math.Sqrt(
            eyeDistanceX * eyeDistanceX + eyeDistanceY * eyeDistanceY);
        if (eyeDistance < 4 || eyeDistance > Math.Max(80, faceWidth * 1.5))
            return false;

        double noseX = landmarks[2].X - landmarks[0].X;
        double noseY = landmarks[2].Y - landmarks[0].Y;
        double cross = eyeDistanceX * noseY - eyeDistanceY * noseX;
        return Math.Abs(cross) > 1;
    }

    private static double CosineSimilarity(float[] left, float[] right)
    {
        if (left.Length != right.Length)
            return -1;
        double dot = 0;
        double leftNormSq = 0;
        double rightNormSq = 0;
        for (int i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftNormSq += left[i] * left[i];
            rightNormSq += right[i] * right[i];
        }
        double denominator = Math.Sqrt(leftNormSq * rightNormSq);
        return denominator > 1e-9 ? dot / denominator : -1;
    }

    private static double IntersectionOverUnion(Rect a, Rect b)
    {
        int left = Math.Max(a.X, b.X);
        int top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width);
        int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        int intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        int union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union == 0 ? 0 : intersection / (double)union;
    }

    private static Rect ScaleFaceRect(Rect face, int frameWidth, int frameHeight)
    {
        int x = Math.Clamp(face.X * frameWidth / DetectionWidth, 0, frameWidth - 1);
        int y = Math.Clamp(face.Y * frameHeight / DetectionHeight, 0, frameHeight - 1);
        int right = Math.Clamp((face.X + face.Width) * frameWidth / DetectionWidth, x + 1, frameWidth);
        int bottom = Math.Clamp((face.Y + face.Height) * frameHeight / DetectionHeight, y + 1, frameHeight);
        return new Rect(x, y, right - x, bottom - y);
    }

    private static void DrawFaceOverlay(
        Mat preview, Rect[] faces, string[] labels, int previewWidth, int previewHeight)
    {
        int faceIndex = 0;
        foreach (var face in faces)
        {
            int x = Math.Clamp(face.X * previewWidth / DetectionWidth, 0, preview.Width - 1);
            int y = Math.Clamp(face.Y * previewHeight / DetectionHeight, 0, preview.Height - 1);
            int right = Math.Clamp((face.X + face.Width) * previewWidth / DetectionWidth, x + 1, preview.Width);
            int bottom = Math.Clamp((face.Y + face.Height) * previewHeight / DetectionHeight, y + 1, preview.Height);
            var box = new Rect(x, y, right - x, bottom - y);

            Cv2.Rectangle(preview, box, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);
            string label = faceIndex < labels.Length ? labels[faceIndex] : "";
            string text = string.IsNullOrEmpty(label) ? "RECONHECENDO" : label;
            Cv2.PutText(preview, text, new Point(box.X, Math.Max(20, box.Y - 8)),
                HersheyFonts.HersheySimplex, 0.55, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);
            faceIndex++;
        }
    }

    /// <summary>
    /// Detecta o gesto de paz (V com indicador e médio) por cor de pele e convexidade.
    /// Exige exatamente 1 falha convexa profunda (o V entre os dedos) com ângulo agudo,
    /// o que é mais específico que "palma aberta" e evita falsos positivos com
    /// outros gestos de 2 dedos (ex: polegar+indicador, "rock" com mindinho).
    /// </summary>
    #if false
    private bool DetectPeaceSign(Mat small)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(small, hsv, ColorConversionCodes.BGR2HSV);

        using var skin1 = new Mat();
        using var skin2 = new Mat();
        using var mask = new Mat();
        // Tom de pele: laranja-avermelhados e próximos (H 0..25 e 160..180).
        Cv2.InRange(hsv, new Scalar(0, 40, 50), new Scalar(25, 255, 255), skin1);
        Cv2.InRange(hsv, new Scalar(160, 40, 50), new Scalar(180, 255, 255), skin2);
        Cv2.BitwiseOr(skin1, skin2, mask);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel, iterations: 2);
        Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);

        Cv2.FindContours(mask, out Point[][] contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        double bestArea = 0;
        Rect bestBox = default;
        Point[] bestContour = Array.Empty<Point>();
        foreach (var contour in contours)
        {
            double area = Cv2.ContourArea(contour);
            if (area > bestArea)
            {
                bestArea = area;
                bestBox = Cv2.BoundingRect(contour);
                bestContour = contour;
            }
        }

        bool visible = false;
        if (bestBox.Width >= 30 && bestBox.Height >= 30)
        {
            double pct = bestArea / (DetectionWidth * (double)DetectionHeight) * 100.0;
            double aspect = bestBox.Width / (double)bestBox.Height;
            visible = pct >= _config.PalmMinAreaPct && aspect is > 0.35 and < 2.8;

            if (visible)
            {
                int[] hull = Cv2.ConvexHullIndices(bestContour);
                Vec4i[] defects = Cv2.ConvexityDefects(bestContour, hull);
                int validGaps = 0;
                foreach (Vec4i defect in defects)
                {
                    Point start = bestContour[defect.Item0];
                    Point end = bestContour[defect.Item1];
                    Point far = bestContour[defect.Item2];
                    double startDistance = Distance(start, far);
                    double endDistance = Distance(end, far);
                    double span = Distance(start, end);
                    double cosine = (startDistance * startDistance + endDistance * endDistance - span * span) /
                                    Math.Max(1, 2 * startDistance * endDistance);
                    double angle = Math.Acos(Math.Clamp(cosine, -1, 1)) * 180.0 / Math.PI;
                    double depth = defect.Item3 / 256.0;

                    // Critérios para o V do sinal de paz:
                    // - Profundidade > 12 px (falha bem marcada)
                    // - Ângulo < 80° (V bem fechado, típico do peace sign)
                    // - O ponto "far" deve estar na metade superior do contorno (região dos dedos)
                    // - Os dois lados do V devem ter comprimento similar (dedos pares)
                    bool farInUpperHalf = far.Y < bestBox.Y + bestBox.Height * 0.6;
                    bool sidesSimilar = Math.Abs(startDistance - endDistance) < Math.Max(startDistance, endDistance) * 0.5;
                    if (depth > 12 && angle < 80 && farInUpperHalf && sidesSimilar)
                        validGaps++;
                }
                // Peace sign = exatamente 1 V bem formado entre indicador e médio
                visible = validGaps == 1;
            }
        }

        // Debounce por tempo: gesto visível por 0,4 s contínuos (funciona em 10 ou 30 fps).
        if (visible)
        {
            if (_palmSince == DateTime.MinValue)
                _palmSince = DateTime.Now;
            return (DateTime.Now - _palmSince).TotalMilliseconds >= PalmHoldMs;
        }

        _palmSince = DateTime.MinValue;
        return false;
    }
    #endif

    private static double Distance(Point left, Point right)
    {
        double x = left.X - right.X;
        double y = left.Y - right.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static void Accumulate(ref long field, long value)
        => Interlocked.Add(ref field, value);

    /// <summary>Heartbeat de saúde/desempenho: relatório a cada 15 s no log.</summary>
    private void ReportStatsIfDue()
    {
        if ((DateTime.Now - _statsSince).TotalSeconds < 15)
            return;

        lock (_statsLock)
        {
            if ((DateTime.Now - _statsSince).TotalSeconds < 15)
                return;

            double secs = Math.Max((DateTime.Now - _statsSince).TotalSeconds, 0.001);
            double loopsPerSec = _loops / secs;
            double readAvg = _loops > 0 ? _readMs / (double)_loops : 0;
            double detectAvg = _loops > 0 ? _detectMs / (double)_loops : 0;
            double saveTotal = _saveMs;

            // fps do rosto vem do pump (thread separada), então só aparece
            // quando os índices são realmente diferentes.
            long faceTotal = Interlocked.Read(ref _faceFrames);
            long faceWindow = faceTotal - _lastFaceFramesTotal;
            _lastFaceFramesTotal = faceTotal;
            double faceFps = _config.FaceCameraIndex == _config.CameraIndex
                ? loopsPerSec
                : faceWindow / secs;
            string faceFpsText = _config.FaceCameraIndex == _config.CameraIndex
                ? "n/d (mesma câmera)"
                : $"{faceFps:0.0} fps";

            // fps do PREVIEW entregue à UI, que é o que o usuário enxerga.
            long previewTotal = Interlocked.Read(ref _previewFrames);
            long previewWindow = previewTotal - _lastPreviewFramesTotal;
            _lastPreviewFramesTotal = previewTotal;
            double previewFps = _previewWanted ? previewWindow / secs : 0;

            LoggerService.Info(
                $"STATS: {loopsPerSec:0.0} it/s | leitura {readAvg:0.00}ms | " +
                $"detecção {detectAvg:0.00}ms | tempo_salvando {saveTotal:0}ms | " +
                $"fotos {_lastCaptureCount} | preview {previewFps:0.0} fps | " +
                $"rosto {faceFpsText} | " +
                $"heap_gerenciado {GC.GetTotalMemory(false) / 1024 / 1024}MB");

            _statsSince = DateTime.Now;
            _loops = _readMs = _detectMs = _saveMs = _lastCaptureCount = 0;
        }
    }

    private static string CreateUniquePhotoPath(string dayDir, DateTime now, string safeUser)
    {
        string baseName = $"{now:yyyyMMdd_HHmmss}_{safeUser}";
        string path = Path.Combine(dayDir, baseName + ".jpg");
        if (!File.Exists(path))
            return path;

        for (int suffix = 1; suffix < 1000; suffix++)
        {
            path = Path.Combine(dayDir, $"{baseName}_{suffix:000}.jpg");
            if (!File.Exists(path))
                return path;
        }
        return Path.Combine(dayDir, $"{baseName}_{Guid.NewGuid():N}.jpg");
    }

    private static Mat ComposeCaptureFrame(Mat partFrame, Mat faceFrame, Rect[] faceBoxes)
    {
        var canvas = partFrame.Clone();
        if (faceBoxes.Length != 1)
        {
            LoggerService.Warn($"Composição ignorada: {faceBoxes.Length} caixas de rosto (é preciso exatamente 1).");
            return canvas;
        }

        Rect faceRect = ScaleFaceRect(faceBoxes[0], faceFrame.Width, faceFrame.Height);
        if (faceRect.Width <= 0 || faceRect.Height <= 0 || faceRect.X < 0 || faceRect.Y < 0 ||
            faceRect.Right > faceFrame.Width || faceRect.Bottom > faceFrame.Height)
        {
            LoggerService.Warn($"Composição ignorada: caixa de rosto inválida após o escalonamento ({faceRect}).");
            return canvas;
        }

        using var faceCrop = new Mat(faceFrame, faceRect);
        if (faceCrop.Empty())
        {
            LoggerService.Warn("Composição ignorada: recorte do rosto saiu vazio.");
            return canvas;
        }

        int maxWidth = Math.Max(160, canvas.Width * 32 / 100);
        int maxHeight = Math.Max(120, canvas.Height * 32 / 100);
        double scale = Math.Min(
            maxWidth / (double)Math.Max(1, faceCrop.Width),
            maxHeight / (double)Math.Max(1, faceCrop.Height));
        int width = Math.Clamp((int)Math.Round(faceCrop.Width * scale), 1, canvas.Width);
        int height = Math.Clamp((int)Math.Round(faceCrop.Height * scale), 1, canvas.Height);

        var outer = new Rect(
            Math.Max(0, canvas.Width - width - 20),
            20,
            width,
            height);

        // Retângulo interno (dentro da borda branca) — é ali que o rosto é copiado.
        var inner = new Rect(outer.X + 3, outer.Y + 3,
            Math.Max(1, outer.Width - 6), Math.Max(1, outer.Height - 6));

        using var resizedFace = new Mat();
        Cv2.Resize(faceCrop, resizedFace, new Size(inner.Width, inner.Height));

        if (resizedFace.Size() != inner.Size)
        {
            LoggerService.Warn(
                $"Composição ignorada: redimensionamento do rosto ficou {resizedFace.Size()} e o destino é {inner.Size}.");
            return canvas;
        }

        Cv2.Rectangle(canvas, outer, new Scalar(255, 255, 255), 4, LineTypes.AntiAlias);

        using (var destination = new Mat(canvas, inner))
            resizedFace.CopyTo(destination);

        return canvas;
    }

    private void CaptureAndSave(Mat frame, string trigger, Mat? faceFrame, Rect[] faceBoxes)
    {
        bool automaticFaceTrigger = string.Equals(trigger, "Rosto", StringComparison.OrdinalIgnoreCase);
        bool forceFace = _forceFaceInNextCapture;
        if (forceFace) _forceFaceInNextCapture = false; // Consome a flag
        
        bool faceTrigger = automaticFaceTrigger ||
            string.Equals(trigger, "Rosto manual", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trigger, "Paz", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trigger, "Manual c/ Rosto", StringComparison.OrdinalIgnoreCase) ||
            forceFace;
        // Uma foto manual, por Palma ou intervalo também reinicia o dwell
        // facial. Sem isso, com cooldown configurado em zero, uma captura
        // manual poderia ser seguida imediatamente por outra automática.
        if (!automaticFaceTrigger)
            ResetFaceDwell();

        DateTime now = DateTime.Now;
        if ((now - _lastCapture).TotalSeconds < Math.Max(0, _config.CooldownSeconds))
            return;

        // Para um disparo facial, use exatamente a identidade que liberou o
        // gate. O usuário ativo pode ser alterado pela UI entre a detecção e
        // a gravação; usar ActiveUserDisplayName aqui atribuiria a foto à
        // pessoa errada.
        string user = automaticFaceTrigger && !string.IsNullOrEmpty(_faceCaptureUser)
            ? _faceCaptureUser
            : _users.ActiveUserDisplayName;
        _lastCapture = now;

        PhotoCaptureStarted?.Invoke(trigger); // LED verde: câmera "tirando a foto"

        try
        {
            string dir = _config.OutputFolder;
            if (string.IsNullOrEmpty(dir))
                dir = ConfigService.DefaultOutputFolder;

            string safeUser = string.Join("_", user.Split(Path.GetInvalidFileNameChars()));
            if (string.IsNullOrEmpty(safeUser))
                safeUser = "usuario";

            // Pastas: {Ano} / {Mês} / {Dia} — sem o usuário no caminho (o operador
            // fica identificado no carimbo e no nome do arquivo, não na estrutura de pastas).
            string[] meses =
            {
                "Janeiro", "Fevereiro", "Março", "Abril", "Maio", "Junho",
                "Julho", "Agosto", "Setembro", "Outubro", "Novembro", "Dezembro",
            };
            string dayDir = Path.Combine(
                dir,
                now.Year.ToString(),
                meses[now.Month - 1],
                now.Day.ToString("00"));
            Directory.CreateDirectory(dayDir);

            string file = CreateUniquePhotoPath(dayDir, now, safeUser);

            // Para disparos forçados (tecla P), usa a maior face detectada mesmo se houver várias.
            // Para disparos automáticos de rosto, exige exatamente 1 face (confiabilidade).
            bool useFace = faceTrigger && faceFrame != null && !faceFrame.Empty() && faceBoxes.Length > 0;
            Rect[] composeBoxes = faceBoxes;
            if (useFace && faceBoxes.Length > 1)
            {
                // Pega a maior face
                var largest = faceBoxes.OrderByDescending(r => r.Width * r.Height).First();
                composeBoxes = new[] { largest };
            }

            using var composedFrame = useFace && faceFrame != null
                ? ComposeCaptureFrame(frame, faceFrame, composeBoxes)
                : null;
            if (faceTrigger && composedFrame == null)
                LoggerService.Warn($"Câmera do rosto indisponível durante {trigger}; salvando apenas a peça");
            Mat imageToSave = composedFrame ?? frame;
            using var annotated = OverlayRenderer.Annotate(imageToSave, now, user, trigger);
            bool ok = Cv2.ImWrite(file, annotated);

            if (!ok)
            {
                LoggerService.Error($"Falha ao gravar imagem: {file}");
                ErrorOccurred?.Invoke("Falha ao gravar imagem: " + file);
                return;
            }

            LoggerService.Info($"Foto salva: {file} ({trigger})");
            // Uma foto manual, por Palma ou outra também bloqueia outro disparo
            // facial recente da mesma pessoa.
            _lastPhotoByUser[user] = now;
            Interlocked.Increment(ref _lastCaptureCount);
            StatusChanged?.Invoke($"Foto: {Path.GetFileName(file)}");
            PhotoCaptured?.Invoke(new CapturedPhoto(file, user, now, trigger));
        }
        catch (Exception ex)
        {
            LoggerService.Error("Falha ao salvar foto", ex);
            ErrorOccurred?.Invoke("Falha ao salvar foto: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        bool stopped = Stop();
        _users.UsersChanged -= ReloadFaceGallery;
        // Garante liberação mesmo se Stop() não completou
        if (!stopped)
        {
            // Não descarta DNN/native buffers enquanto a thread ainda os usa.
            // O processo está encerrando; o SO libera os recursos ao final.
            LoggerService.Warn("Recursos do motor serão liberados quando a thread encerrar.");
            return;
        }
        _writeSignal.Dispose();
        _faceRecognizer.Dispose();
        _yunet?.Dispose();
    }
}