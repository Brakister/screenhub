# Build e publicação do ScreenLab

## Compilar (Release)
```powershell
dotnet build src\ScreenLab\ScreenLab.csproj -c Release
```

## Publicar executável autônomo (recomendado p/ produção)
Cria a pasta `publicacao` com tudo que o sistema precisa — copie inteira
para o computador que vai rodar 24/7 (não precisa de .NET instalado):
```powershell
dotnet publish src\ScreenLab\ScreenLab.csproj -c Release -r win-x64 --self-contained true -o publicacao
```

## Testar na máquina atual
```powershell
.\src\ScreenLab\bin\Release\net8.0-windows\ScreenLab.exe
```