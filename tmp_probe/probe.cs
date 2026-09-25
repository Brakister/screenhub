using OpenCvSharp;
using OpenCvSharp.Dnn;

// Compara a deteccao YuNet: caminho ATUAL (estica 320x240 -> 320x320) vs
// letterbox (preserva proporcao). Mede quantidade por foto e aspecto dos boxes.

const string ModelDir = @"C:\screenlab\src\ScreenLab\Data\models";
const int DetW = 320, DetH = 240;
string[] names = { "cls_8", "cls_16", "cls_32", "obj_8", "obj_16", "obj_32",
                   "bbox_8", "bbox_16", "bbox_32", "kps_8", "kps_16", "kps_32" };
int[] strides = { 8, 16, 32 };
using var yunet = CvDnn.ReadNetFromOnnx(Path.Combine(ModelDir, "face_detection_yunet_2023mar.onnx"));

static double Iou(Rect a, Rect b)
{
    int l = Math.Max(a.X, b.X), t = Math.Max(a.Y, b.Y);
    int r = Math.Min(a.X + a.Width, b.X + b.Width), bo = Math.Min(a.Y + a.Height, b.Y + b.Height);
    int i = Math.Max(0, r - l) * Math.Max(0, bo - t);
    int u = a.Width * a.Height + b.Width * b.Height - i;
    return u == 0 ? 0 : i / (double)u;
}

var letterbox = true;

List<(Rect Box, Point2f[] Kps, float Score)> Run(Mat small, float thr, float nms, bool useLetterbox)
{
    Mat entrada;
    float scale;
    int padX = 0, padY = 0;
    if (useLetterbox)
    {
        scale = Math.Min(320f / small.Width, 320f / small.Height);
        int nw = (int)Math.Round(small.Width * scale), nh = (int)Math.Round(small.Height * scale);
        padX = (320 - nw) / 2;
        padY = (320 - nh) / 2;
        using var rs = new Mat();
        Cv2.Resize(small, rs, new OpenCvSharp.Size(nw, nh));
        entrada = new Mat(320, 320, MatType.CV_8UC3, Scalar.All(0));
        using var dst = new Mat(entrada, new Rect(padX, padY, nw, nh));
        rs.CopyTo(dst);
    }
    else
    {
        using var rs = new Mat();
        Cv2.Resize(small, rs, new OpenCvSharp.Size(320, 320));
        entrada = rs.Clone();
        scale = 1f;
    }

    using var blob = CvDnn.BlobFromImage(entrada, 1.0, new OpenCvSharp.Size(320, 320), new Scalar(), false, false);
    yunet.SetInput(blob);
    var outs = new List<Mat>();
    for (int i = 0; i < names.Length; i++) outs.Add(new Mat());
    yunet.Forward(outs, names);

    float mapX(float netX) => useLetterbox ? (netX - padX) / scale : netX * DetW / 320f;
    float mapY(float netY) => useLetterbox ? (netY - padY) / scale : netY * DetH / 320f;

    var raw = new List<(Rect, Point2f[], float)>();
    for (int s = 0; s < 3; s++)
    {
        int stride = strides[s], g = 320 / stride, grid = g * g;
        using var cls = outs[s].Reshape(1, grid);
        using var obj = outs[s + 3].Reshape(1, grid);
        using var bbox = outs[s + 6].Reshape(1, grid);
        using var kps = outs[s + 9].Reshape(1, grid);
        for (int i = 0; i < grid; i++)
        {
            float sc = MathF.Sqrt(Math.Clamp(cls.At<float>(i, 0), 0, 1) * Math.Clamp(obj.At<float>(i, 0), 0, 1));
            if (sc < thr) continue;
            int row = i / g, col = i % g;
            float cx = (col + bbox.At<float>(i, 0)) * stride, cy = (row + bbox.At<float>(i, 1)) * stride;
            float w = MathF.Exp(bbox.At<float>(i, 2)) * stride, h = MathF.Exp(bbox.At<float>(i, 3)) * stride;
            float x0 = mapX(cx - w / 2), y0 = mapY(cy - h / 2);
            float x1 = mapX(cx + w / 2), y1 = mapY(cy + h / 2);
            int x = Math.Clamp((int)x0, 0, DetW - 1);
            int y = Math.Clamp((int)y0, 0, DetH - 1);
            int r = Math.Clamp((int)x1, x + 1, DetW);
            int b = Math.Clamp((int)y1, y + 1, DetH);
            var p = new Point2f[5];
            for (int k = 0; k < 5; k++)
                p[k] = new Point2f(mapX((kps.At<float>(i, k * 2) + col) * stride),
                                   mapY((kps.At<float>(i, k * 2 + 1) + row) * stride));
            raw.Add((new Rect(x, y, r - x, b - y), p, sc));
        }
    }
    foreach (var m in outs) m.Dispose();
    entrada.Dispose();
    var kept = new List<(Rect, Point2f[], float)>();
    foreach (var c in raw.OrderByDescending(c => c.Item3))
        if (kept.All(k => Iou(k.Item1, c.Item1) < nms)) kept.Add(c);
    return kept;
}

var dir = @"C:\Users\VictorCiorla\OneDrive - Starke Parts Comercio Imp e Exp de Peças Automotivas LTDA\Imagens\ScreenLab";
var files = Directory.GetFiles(dir, "*.jpg", SearchOption.AllDirectories).OrderBy(x => x).ToList();

static string Fmt(List<(Rect, Point2f[], float)> d) => d.Count == 0
    ? "-"
    : string.Join(" ", d.Take(3).Select(x =>
        $"({x.Item1.X},{x.Item1.Y},{x.Item1.Width}x{x.Item1.Height}) asp={(float)x.Item1.Width / x.Item1.Height:F2} s={x.Item3:F2}"));

foreach (float thr in new[] { 0.6f, 0.5f, 0.4f })
{
    Console.WriteLine($"##### limiar {thr:F1} | NMS IoU 0.30 #####");
    Console.WriteLine("foto                              | ATUAL (estica)              | LETTERBOX (proporcao)");
    int totA = 0, totL = 0;
    foreach (var f in files)
    {
        using var frame = Cv2.ImRead(f);
        using var small = new Mat();
        Cv2.Resize(frame, small, new OpenCvSharp.Size(DetW, DetH));
        var a = Run(small, thr, 0.30f, false);
        var l = Run(small, thr, 0.30f, true);
        totA += a.Count;
        totL += l.Count;
        Console.WriteLine($"{Path.GetFileName(f),-32} | {Fmt(a),-40} | {Fmt(l)}");
    }
    Console.WriteLine($"TOTAL: atual={totA}  letterbox={totL}\n");
}
return 0;
