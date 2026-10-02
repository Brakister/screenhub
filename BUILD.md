# Build e publicação do ScreenLab

## Compilar (Release)
```powershell
dotnet build src\ScreenLab\ScreenLab.csproj -c Release
```

## Publicar o executável autônomo
Gera **um `.exe` só** em `publish\` — é esse arquivo que vai para o GitHub
Release e para o computador que vai rodar 24/7 (não precisa de .NET instalado).

O `ScreenLab.csproj` já traz `PublishSingleFile`, `SelfContained` e
`IncludeAllContentForSelfExtract`, então os modelos ONNX (detector YuNet +
reconhecimento ArcFace, ~249 MB) e as haarcascades vão embutidos **dentro** do
exe. Quem baixar só o `.exe` tem a detecção facial funcionando.

```powershell
dotnet publish src\ScreenLab\ScreenLab.csproj -c Release -r win-x64 --self-contained true -o publish
```

Saída: `publish\ScreenLab.exe` (~352 MB).

## Gerar o instalador
Precisa do [Inno Setup 6](https://jrsoftware.org/isinfo.php). O script lê o
`publish\ScreenLab.exe` e escreve o setup em `publish\`:

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\ScreenLab.iss
```

Saída: `publish\ScreenLab-Setup-<versão>.exe`.

## Subir o release
A versão precisa estar incrementada em **dois** lugares — `ScreenLab.csproj`
(`<Version>`) e `installer\ScreenLab.iss` (`MyAppVersion`):

```powershell
git tag -a v1.4.0 -m "v1.4.0 - <resumo>"
git push origin main
git push origin v1.4.0

gh release create v1.4.0 `
  "publish\ScreenLab.exe#ScreenLab.exe" `
  "publish\ScreenLab-Setup-1.4.0.exe#ScreenLab-Setup-1.4.0.exe" `
  --title "ScreenLab v1.4.0" --notes-file notas.md --latest
```

## Testar na máquina atual
```powershell
.\src\ScreenLab\bin\Release\net8.0-windows\win-x64\ScreenLab.exe
```

## Diagnóstico
Log diário em `%APPDATA%\ScreenLab\logs\ScreenLab_aaaaMMdd.log`. A cada
janela de 15 s sai uma linha `STATS` com iterações/s, média de leitura,
detecção (e o **pior** caso da janela), tempo salvando, **fps do preview** e
**fps da câmera do rosto** — esses dois últimos são os que acusam câmera travada.

A média de `detecção` não serve para achar travamento: o reconhecimento caro roda
de vez em quando, então a média se dilui e some. Olhe o `(pior NNN ms)`: é ele
que mostra o congelamento que o operador vê.
