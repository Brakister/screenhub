using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using OpenCvSharp;
using ScreenLab.Services;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace ScreenLab.Capture;

/// <summary>
/// Motor de captura 24/7: lê a câmera em um thread de fundo, aplica os modos
/// de disparo (movimento, rosto, intervalo, manual), carimba a foto com
/// data/hora + usuário e salva em disco. Projetado para baixo consumo.
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private const int DetectionWidth = 320;
    private const int DetectionHeight = 240;
    private const int PreviewWidth = 800;   // pré-visualização em resolução reduzida = leve e rápido
    private const int PreviewHeight = 450;
    private const int PreviewMinIntervalMs = 45; // ~22 fps
    private const int LoopPauseMsWithPreview = 10;
    private const int LoopPauseMsWithoutPreview = 90; // oculto: ~10 leituras/s p/ economizar CPU

    private readonly AppConfig _config;
    private readonly UserManager _users;

    private readonly object _stateLock = new();
    private VideoCapture? _capture;
    private Thread? _thread;
    private CancellationTokenSource? _cts;

    private DateTime _palmSince = DateTime.MinValue;
    private const int PalmHoldMs = 400; // palma precisa ficar visível por 0,4 s contínuos
    private readonly Stopwatch _faceWatch = Stopwatch.StartNew();
    private readonly Stopwatch _previewWatch = Stopwatch.StartNew();
    private DateTime _lastCapture = DateTime.MinValue;
    private DateTime _lastIntervalCapture = DateTime.MinValue;
    private DateTime _lastCameraFailLog = DateTime.MinValue;
    private int _cameraFailStreak;

    // Estatísticas operacionais (relatório periódico no log — útil para 24/7)
    private readonly object _statsLock = new();
    private DateTime _statsSince = DateTime.Now;
    private long _loops, _readMs, _detectMs, _saveMs, _lastCaptureCount;
    private volatile string _pendingManualTrigger = "";
    private volatile bool _paused;
    private volatile bool _running;
    private volatile bool _previewWanted = true;

    private static readonly CascadeClassifier? _faceCascade = LoadFaceCascade();

    public event Action<Mat>? PreviewFrame;       // frame para pré-visualização (thread de fundo)
    public event Action<string>? PhotoCaptureStarted; // disparado quando a captura começa (LED verde)
    public event Action<CapturedPhoto>? PhotoCaptured;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<bool>? RunningChanged;

    public CaptureEngine(AppConfig config, UserManager users)
    {
        _config = config;
        _users = users;
    }

    public bool IsRunning => _running;
    public bool IsPaused => _paused;

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

    public void Start()
    {
        lock (_stateLock)
        {
            if (_running) return;
            _running = true;
            _lastIntervalCapture = DateTime.Now;
            _cts = new CancellationTokenSource();
        }

        _thread = new Thread(Loop) { IsBackground = true, Name = "ScreenLab.Capture" };
        _thread.Start();
        LoggerService.Info("Motor de captura iniciado");
        StatusChanged?.Invoke("Iniciando câmera...");
        RunningChanged?.Invoke(true);
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_running) return;
            _running = false;
            _cts?.Cancel();
        }

        _thread?.Join(3000);
        lock (_stateLock)
        {
            _cts?.Dispose();
            _cts = null;
            _capture?.Dispose();
            _capture = null;
            _frameBuf?.Dispose();
            _frameBuf = null;
            _previewBuf?.Dispose();
            _previewBuf = null;
        }

        LoggerService.Info("Motor de captura parado");
        StatusChanged?.Invoke("Parado");
        RunningChanged?.Invoke(false);
    }

    /// <summary>Reabre a câmera (usado ao trocar o índice da câmera).</summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    public void SetPreviewWanted(bool wanted) => _previewWanted = wanted;

    /// <summary>Buffer de quadro reutilizado (evita alocar/liberar memória a cada frame).</summary>
    private Mat? _frameBuf;

    /// <summary>Buffer reutilizado para a pré-visualização em baixa resolução.</summary>
    private Mat? _previewBuf;

    private Mat FrameBuffer => _frameBuf ??= new Mat();
    private Mat PreviewBuffer => _previewBuf ??= new Mat();

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
                var swRead = Stopwatch.StartNew();
                bool readOk = capture.Read(frame) && !frame.Empty();
                swRead.Stop();

                if (!readOk)
                {
                    // Câmera desconectada? Tenta reabrir no próximo ciclo.
                    TryDisposeCapture();
                    Thread.Sleep(500);
                    continue;
                }

                Accumulate(ref _readMs, swRead.ElapsedMilliseconds);

                var swDetect = Stopwatch.StartNew();
                bool fire = false;
                string trigger = _pendingManualTrigger;
                if (!string.IsNullOrEmpty(trigger))
                {
                    _pendingManualTrigger = "";
                    fire = true;
                }
                else if (Detect(frame, out trigger))
                {
                    fire = true;
                }
                else if (_config.IntervalEnabled &&
                         (DateTime.Now - _lastIntervalCapture).TotalSeconds >= _config.IntervalSeconds)
                {
                    _lastIntervalCapture = DateTime.Now;
                    trigger = "Intervalo";
                    fire = true;
                }
                swDetect.Stop();
                Accumulate(ref _detectMs, swDetect.ElapsedMilliseconds);

                if (fire)
                {
                    var swSave = Stopwatch.StartNew();
                    CaptureAndSave(frame, trigger);
                    swSave.Stop();
                    Accumulate(ref _saveMs, swSave.ElapsedMilliseconds);
                }

                Accumulate(ref _loops, 1);
                ReportStatsIfDue();

                if (_previewWanted && _previewWatch.ElapsedMilliseconds >= PreviewMinIntervalMs)
                {
                    _previewWatch.Restart();
                    // Reutiliza um buffer persistente: sem alocar frame por frame (memória estável)
                    // e sem carimbo na prévia (o carimbo vai somente na foto salva).
                    Cv2.Resize(frame, PreviewBuffer, new Size(PreviewWidth, PreviewHeight));
                    PreviewFrame?.Invoke(PreviewBuffer);
                }
            }
            catch (Exception ex)
            {
                LoggerService.Error("Erro no loop de captura", ex);
                ErrorOccurred?.Invoke(ex.Message);
                Thread.Sleep(1000);
            }

            Thread.Sleep(_previewWanted ? LoopPauseMsWithPreview : LoopPauseMsWithoutPreview);
        }
    }

    private bool EnsureCameraOpen()
    {
        var capture = _capture;
        if (capture != null && capture.IsOpened())
        {
            _cameraFailStreak = 0;
            return true;
        }

        try
        {
            var cap = new VideoCapture(_config.CameraIndex);
            if (!cap.IsOpened())
            {
                cap.Dispose();
                _cameraFailStreak++;
                LogCameraUnavailable();
                return false;
            }

            if (_config.VideoWidth > 0)
                cap.Set(VideoCaptureProperties.FrameWidth, _config.VideoWidth);
            if (_config.VideoHeight > 0)
                cap.Set(VideoCaptureProperties.FrameHeight, _config.VideoHeight);

            _capture = cap;
            _cameraFailStreak = 0;
            LoggerService.Info(
                $"Câmera {_config.CameraIndex} aberta em {cap.FrameWidth}x{cap.FrameHeight}");
            StatusChanged?.Invoke($"Câmera {_config.CameraIndex}: {cap.FrameWidth}x{cap.FrameHeight}");
            return true;
        }
        catch (Exception ex)
        {
            LoggerService.Error($"Falha ao abrir câmera {_config.CameraIndex}", ex);
            _cameraFailStreak++;
            LogCameraUnavailable();
            return false;
        }
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
        lock (_stateLock)
        {
            _capture?.Dispose();
            _capture = null;
        }
    }

    /// <summary>Detecção leve de palma da mão e/ou rosto sobre quadro reduzido.</summary>
    private bool Detect(Mat frame, out string trigger)
    {
        trigger = "";
        bool palm = _config.PalmEnabled;
        bool face = _config.FaceEnabled && _faceCascade != null;
        if (!palm && !face)
            return false;

        using var small = new Mat();
        using var gray = new Mat();
        Cv2.Resize(frame, small, new Size(DetectionWidth, DetectionHeight));
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new Size(21, 21), 0);

        if (palm && DetectPalm(small))
        {
            trigger = "Palma";
            return true;
        }

        if (face && _faceWatch.ElapsedMilliseconds >= _config.FaceIntervalMs)
        {
            _faceWatch.Restart();
            var faces = _faceCascade!.DetectMultiScale(
                gray, 1.1, 4, HaarDetectionTypes.ScaleImage, new Size(50, 50));
            if (faces.Length > 0)
            {
                trigger = "Rosto";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detecta palma da mão por cor de pele (HSV) + maior contorno.
    /// Exige que a mancha de pele tenha área mínima e permaneça visível
    /// alguns quadros — assim fotos não saem com qualquer coisa se mexendo.
    /// </summary>
    private bool DetectPalm(Mat small)
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
        foreach (var contour in contours)
        {
            double area = Cv2.ContourArea(contour);
            if (area > bestArea)
            {
                bestArea = area;
                bestBox = Cv2.BoundingRect(contour);
            }
        }

        bool visible = false;
        if (bestBox.Width >= 30 && bestBox.Height >= 30)
        {
            double pct = bestArea / (DetectionWidth * (double)DetectionHeight) * 100.0;
            double aspect = bestBox.Width / (double)bestBox.Height;
            visible = pct >= _config.PalmMinAreaPct && aspect is > 0.35 and < 2.8;
        }

        // Debounce por tempo: palma visível por 0,4 s contínuos (funciona em 10 ou 30 fps).
        if (visible)
        {
            if (_palmSince == DateTime.MinValue)
                _palmSince = DateTime.Now;
            return (DateTime.Now - _palmSince).TotalMilliseconds >= PalmHoldMs;
        }

        _palmSince = DateTime.MinValue;
        return false;
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

            LoggerService.Info(
                $"STATS: {loopsPerSec:0.0} it/s | leitura {readAvg:0.00}ms | " +
                $"detecção {detectAvg:0.00}ms | tempo_salvando {saveTotal:0}ms | " +
                $"fotos {_lastCaptureCount} | heap_gerenciado {GC.GetTotalMemory(false) / 1024 / 1024}MB");

            _statsSince = DateTime.Now;
            _loops = _readMs = _detectMs = _saveMs = _lastCaptureCount = 0;
        }
    }

    private void CaptureAndSave(Mat frame, string trigger)
    {
        if ((DateTime.Now - _lastCapture).TotalSeconds < _config.CooldownSeconds)
            return;

        string user = _users.ActiveUserDisplayName;
        DateTime now = DateTime.Now;
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

            string file = Path.Combine(dayDir, $"{now:yyyyMMdd_HHmmss}_{safeUser}.jpg");

            using var annotated = OverlayRenderer.Annotate(frame, now, user, trigger);
            bool ok = Cv2.ImWrite(file, annotated);

            if (!ok)
            {
                LoggerService.Error($"Falha ao gravar imagem: {file}");
                ErrorOccurred?.Invoke("Falha ao gravar imagem: " + file);
                return;
            }

            LoggerService.Info($"Foto salva: {file} ({trigger})");
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
        Stop();
    }
}