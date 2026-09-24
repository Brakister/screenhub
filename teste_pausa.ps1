$code = @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Pzz {
  public struct RECT { public int L,T,R,B; }
  public struct POINT { public int X,Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  public static void Click(IntPtr main, int cx, int cy) {
    var co = new POINT(); ClientToScreen(main, ref co);
    SetCursorPos(co.X+cx, co.Y+cy);
    mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero);
  }
  public static string[] Dump(IntPtr main) {
    var list = new List<string>();
    var co = new POINT(); ClientToScreen(main, ref co);
    EnumChildWindows(main, (h, l) => {
      GetWindowRect(h, out RECT r);
      var sb = new StringBuilder(96); GetClassName(h, sb, 96);
      var sb2 = new StringBuilder(96); GetWindowText(h, sb2, 96);
      list.Add(String.Format("at=({0},{1}) size={2}x{3} vis={4} class={5,-14} text='{6}'",
        r.L-co.X, r.T-co.Y, r.R-r.L, r.B-r.T, IsWindowVisible(h), sb.ToString(), sb2.ToString()));
      return true;
    }, IntPtr.Zero);
    return list.ToArray();
  }
}
"@
Add-Type -TypeDefinition $code -Language CSharp
Get-Process ScreenLab -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
$logd = Join-Path $env:APPDATA "ScreenLab\logs"
$log = Get-ChildItem $logd -Filter *.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$p = Start-Process -FilePath "C:\screenlab\publicacao\ScreenLab.exe" -PassThru
Start-Sleep -Seconds 8
$h = (Get-Process ScreenLab -ErrorAction SilentlyContinue | Select-Object -First 1).MainWindowHandle
"A) pausa com F8 via teclado (atalho real):"
$wsh = New-Object -ComObject WScript.Shell
$antes = (Get-Content $log.FullName -Raw)
$wsh.SendKeys("{F8}")
Start-Sleep -Seconds 1
$dump = [Pzz]::Dump($h)
$retomar = $dump | Where-Object { $_ -match "Retomar \(F8\)" }
if ($retomar) { "   botao agora: '$retomar'  => PAUSA FUNCIONANDO" }
$es = $dump | Where-Object { $_ -match "Pausar \(F8\)" }
"   (botao 'Pausar' ainda existe? $(if($es){'SIM - NAO pausou'}else{'nao -> pausado OK'}))"
"B) com pausa ativa, ESPACO NÃO tira foto:"
$wsh.SendKeys(" ")
Start-Sleep -Seconds 2
$depois = (Get-Content $log.FullName -Raw)
if ($depois.Length -eq $antes.Length) { "   ESPACO ignorado em pausa: SIM (nenhuma foto)" } else { "   PRODUZIU foto pausado?! BUG" }
"C) F8 de novo retoma:"
$wsh.SendKeys("{F8}")
Start-Sleep -Seconds 1
$dump2 = [Pzz]::Dump($h)
$pausar = $dump2 | Where-Object { $_ -match "Pausar \(F8\)" }
if ($pausar) { "   botao voltou a '$pausar' => RETOMOU OK" }
"D) ESPACO retomado tira foto:"
$antes2 = (Get-Content $log.FullName -Raw)
$wsh.SendKeys(" ")
Start-Sleep -Seconds 3
$depois2 = (Get-Content $log.FullName -Raw)
if ($depois2.Length -gt $antes2.Length) { "   foto tirada após retomar: SIM" } else { "   sem foto após retomar: ?" }
Get-Process ScreenLab -ErrorAction SilentlyContinue | Stop-Process -Force
"FIM"