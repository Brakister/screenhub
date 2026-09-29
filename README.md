# ScreenLab — Captura de fotos com identificação de usuário

Sistema leve em **C# (.NET 8)** que captura fotos **manuais** pela webcam com
**data/hora + nome do usuário** carimbados na imagem. A janela fica **visível**
para os operadores acompanharem a pré-visualização em tempo real.

## Funcionalidades

- ✅ **Captura manual** — a foto só sai ao apertar o **botão**, a tecla
  **ESPAÇO** ou o botão do menu da bandeja.
- ✅ **Contagem regressiva de 1 s** — ao acionar, aparece um "1" sobre a
  pré-visualização e a foto é tirada 1 segundo depois (dá tempo de compor).
- ✅ **Luz de status (LED)** — **vermelho** = esperando (não está tirando foto);
  **verde** = foto em andamento. Fica ao lado da pré-visualização.
- ✅ **Usuários pré-definidos** — selecione quem está operando (combobox).
- ✅ **Carimbo na foto** — `dd/mm/aaaa hh:mm:ss` e `Usuário: Nome` na imagem.
- ✅ **Organização automática** — fotos salvas em
  `Pasta\NomeUsuário-AAAA\Mês\Dia\foto.jpg`.
- ✅ **Pré-visualização a ~30 fps** na janela.
- ✅ **Inicia com o Windows** — opção na tela ou no menu da bandeja.
- ✅ **Bandeja do sistema** — fechar a janela continua rodando em segundo plano
  (opcional; por padrão a janela fica aberta para os operadores).
- ✅ **Logs** — em `%APPDATA%\ScreenLab\logs` (um arquivo por dia).
- ✅ **Configuração** — em `%APPDATA%\ScreenLab\config.json`.

## Como usar

1. Inicie o `ScreenLab.exe` (pasta `publish` ou `bin\Release\net8.0-windows\win-x64`).
2. Selecione o operador na lista **Usuário**.
3. Deixe a janela visível e oriente a câmera para o local desejado.
4. Para fotografar: **botão verde "TIRAR FOTO"** ou tecla **ESPAÇO** — aparece o
   "1" na tela e a foto é tirada 1 segundo depois (LED verde no momento).
5. Para encerrar de vez: menu da bandeja → **Sair**.

Fotos ficam em `%USERPROFILE%\Pictures\ScreenLab` por padrão
(configurável na tela), organizadas em
`Operador-2026\Setembro\24\20260924_154230_Operador.jpg`.

### Teclas

| Tecla      | Ação |
|------------|------|
| `ESPAÇO`   | Tirar foto (com 1 s de previsão) |
| `F9`       | Tirar foto (global, funciona até sem foco na janela) |
| `F8`       | Pausar / Retomar |

## Estrutura do projeto

```
src\ScreenLab\
├── Program.cs                 → entrada, instância única, tratamento de erros
├── Services\
│   ├── ConfigService.cs       → leitura/gravação do config.json
│   ├── LoggerService.cs       → log em arquivo (diário)
│   ├── UserManager.cs         → usuários pré-definidos + usuário ativo
│   └── WindowsStartupService.cs → registro "iniciar com Windows"
├── Capture\
│   ├── CaptureEngine.cs       → thread de captura + leitura da câmera
│   ├── OverlayRenderer.cs     → carimbo data/hora + usuário
│   └── CapturedPhoto.cs       → modelo da foto capturada
└── UI\
    ├── MainForm.cs            → janela principal + LED + bandeja
    ├── LedControl.cs          → luz de status (vermelho/verde)
    └── AppIcons.cs            → ícone gerado em runtime
```

## Compilando

Pré-requisito: **.NET 8 SDK** (ou superior).

```powershell
# Compilar Release
dotnet build src\ScreenLab\ScreenLab.csproj -c Release

# Publicar executável autônomo (sem precisar de .NET instalado no alvo)
dotnet publish src\ScreenLab\ScreenLab.csproj -c Release -r win-x64 --self-contained true -o publish
```

Sai **um `.exe` só** em `publish\ScreenLab.exe` (~146 MB), com os modelos
embutidos dentro. Copie esse arquivo para o computador destino e rode.
Para gerar o instalador `.exe` de fato, veja [BUILD.md](BUILD.md).

## Ajustes rápidos (config.json)

Arquivo: `%APPDATA%\ScreenLab\config.json`

| Campo | Descrição | Padrão |
|-------|-----------|--------|
| `cameraIndex` | Índice da webcam (0 = primeira) | 0 |
| `cooldownSeconds` | Pausa mínima entre fotos (evita disparo duplo) | 5 |
| `videoWidth/videoHeight` | Resolução da captura | 1920×1080 |
| `faceCameraIndex` | Índice da 2ª webcam (detecção de rosto) | — |
| `faceEnabled` | Liga a câmera de rosto | false |
| `faceVideoWidth/faceVideoHeight` | Resolução da câmera de rosto | 640×480 |
| `faceIntervalMs` | Intervalo entre reconhecimentos (ms) | 400 |
| `faceCaptureRequireKnown` | Só fotografa pessoa já cadastrada | true |
| `faceMultiPerson` | Várias pessoas no quadro: 1 foto para cada, cada uma com o seu nome | true |
| `faceAutoLearnNew` | Aprende sozinho um rosto novo estável no operador escolhido na tela | true |
| `faceConfirmSeconds` | Tempo de rosto firme antes da foto (s) | 2 |
| `faceCaptureDwellSeconds` | Tempo de rosto presente antes da foto (s) | 3 |
| `faceCaptureCooldownSeconds` | Intervalo entre fotos da mesma pessoa (s) | 300 |
| `outputFolder` | Pasta de destino das fotos | `...\Pictures\ScreenLab` |

A pasta de fotos é criada automaticamente como
`{nome do usuário}-{ano}\{mês}\{dia}`.

### Várias pessoas no quadro

Com duas ou mais pessoas na frente, o app resolve o quadro inteiro de uma vez,
em vez de decidir rosto por rosto, porque **cada nome ocupa no máximo um rosto**.
É a atribuição ótima (algoritmo húngaro), não a gulosa: a gulosa pode travar o
rosto certo no nome certo e empurrar o outro para o nome errado.

Cada pessoa confirmada ganha a sua própria foto, recortada e carimbada com o
nome dela. Se duas forem parecidas demais para o app decidir com folga, ele
**não fotografa** — trocar os nomes seria pior do que não fotografar. O
operador ativo não é alterado nesse modo: com várias pessoas na frente, "quem é
o operador agora" não tem resposta estável.

O aprendizado de rosto novo tem trava de segurança: só acontece nos 2 minutos
seguintes a você escolher o operador na tela, e só depois de 8 quadros seguidos
com o mesmo rosto (similaridade ≥ 0,75 entre eles), no máximo 1 pose a cada
10 min. Um visitante que passa na frente não é aprendido sozinho. Se entrar
gente errada, apague com **Redefinir galeria**.

## Solução de problemas

- **"Câmera X indisponível"** — verifique conexão/drivers e teste outro índice
  no botão **Testar**. O sistema tenta reabrir automaticamente a cada 2 s.
- **Preview travado ou "atrasado"** — confira a linha `STATS` no log: `preview`
  é o fps entregue à tela e `rosto` o da 2ª webcam. Abaixo de ~20 fps num dos
  dois, nenhum ajuste do app resolve: é a câmera ou o cabo. Duas webcams 1080p
  no mesmo hub USB 2.0 saturam a banda — a de rosto deve ficar em 640×480
  (é o padrão). O log também avisa sozinho quando a resolução pedida é
  ignorada pelo driver.
- **Foto não sai ao apertar o botão** — confira se a captura não está **Pausada**
  (LED permanece vermelho e o botão fica desabilitado) ou se ainda não passou o
  tempo mínimo entre fotos.
- **Logs** — consulte `%APPDATA%\ScreenLab\logs` para diagnosticar.

## Considerações de operação 24/7

- O sistema mantém a câmera aberta — não deixe o PC suspender
  (energia > suspensão desativada) se precisar de operação contínua.
- O app é instância única: rodar duas vezes apenas mostra a janela existente.
- A janela abre **visível** para os operadores. Fechando a janela, o app
  continua na bandeja; para sair de verdade use o menu da bandeja → **Sair**.