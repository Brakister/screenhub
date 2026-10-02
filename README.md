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
- ✅ **Fechar encerra de vez** — clicar no **X** fecha o programa. Só o
  **minimizar** manda para a bandeja, onde a captura continua.
- ✅ **Logs** — em `%APPDATA%\ScreenLab\logs` (um arquivo por dia).
- ✅ **Configuração** — em `%APPDATA%\ScreenLab\config.json`.

## Como usar

1. Inicie o `ScreenLab.exe` (pasta `publish` ou `bin\Release\net8.0-windows\win-x64`).
2. Selecione o operador na lista **Usuário**.
3. Deixe a janela visível e oriente a câmera para o local desejado.
4. Para fotografar: **botão verde "TIRAR FOTO"** ou tecla **ESPAÇO** — aparece o
   "1" na tela e a foto é tirada 1 segundo depois (LED verde no momento).
5. Para encerrar de vez: clique no **X** da janela. (Minimizar apenas manda o
   app para a bandeja e a captura continua.)

Fotos ficam em `%USERPROFILE%\Pictures\ScreenLab` por padrão
(configurável na tela), organizadas em
`Operador-2026\Setembro\24\20260924_154230_Operador.jpg`.

### Teclas

| Tecla      | Ação |
|------------|------|
| `ESPAÇO`   | Tirar foto (com 1 s de previsão) |
| `F9`       | Tirar foto (global, funciona até sem foco na janela) |
| `F8`       | Pausar / Retomar |
| `F10`      | Aprender o rosto que está na frente da câmera |
| `F11`      | Incluir / excluir o rosto na foto (liga e desliga o recorte do rosto) |

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
│   ├── CaptureEngine.cs       → thread de captura + leitura da câmera + reconhecimento
│   ├── OverlayRenderer.cs     → carimbo data/hora + usuário
│   └── CapturedPhoto.cs       → modelo da foto capturada
├── Recognition\
│   └── FaceRecognizerService.cs → embeddings faciais (ArcFace) + galeria
├── Data\models\
│   ├── face_detection_yunet_2023mar.onnx → detector de rosto
│   └── arcfaceresnet100-8.onnx           → reconhecimento (512-d)
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

Sai **um `.exe` só** em `publish\ScreenLab.exe` (~352 MB), com os modelos
embutidos dentro (a maior parte é o modelo ArcFace, ~249 MB). Copie esse arquivo
para o computador destino e rode. Para gerar o instalador `.exe` de fato, veja
[BUILD.md](BUILD.md).

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

### Reconhecimento facial

- **Detector:** YuNet (`face_detection_yunet_2023mar.onnx`), que acha o rosto e
  os 5 pontos de referência.
- **Reconhecimento:** ArcFace (`arcfaceresnet100-8.onnx`, embedding 512-d), com
  o rosto alinhado pelos pontos antes de gerar o vetor.
- **Galeria:** até 10 poses por pessoa. O cadastro guia as poses na tela; uma
  pose igual à anterior é recusada com "VARIE MAIS A POSE" (as poses só precisam
  **mudar** em relação à captura anterior; variação leve já basta).
- **Onde estão os modelos:** embutidos no `.exe`. A galeria fica no
  `config.json` (um vetor de 512 números por pose).
- **Custo:** cada reconhecimento leva ~70-250 ms nesta classe de CPU. Por isso o
  app só reavalia quando a cena muda: com a identidade já confirmada e ninguém se
  mexendo, ele reavalia a cada 3 s em vez de 2,5 vezes por segundo.

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

Antes de atribuir os nomes, o app descarta caixas que são **o mesmo rosto duas
vezes** no quadro (cosseno ≥ 0,90 entre elas). Isso acontece quando a câmera
enxerga o próprio preview na tela ou um reflexo no vidro: o rosto "de volta" é
quase idêntico ao real, e sem esse descarte o app dava um nome para cada caixa —
o operador saía carimbado também com o nome do colega. O rosto maior é o que
fica; o menor (o reflexo) é ignorado.

## Solução de problemas

- **"Câmera X indisponível"** — verifique conexão/drivers e teste outro índice
  no botão **Testar**. O sistema tenta reabrir automaticamente a cada 2 s.
- **Preview travado ou "atrasado"** — confira a linha `STATS` no log: `preview`
  é o fps entregue à tela e `rosto` o da 2ª webcam. Abaixo de ~20 fps num dos
  dois, nenhum ajuste do app resolve: é a câmera ou o cabo. Duas webcams 1080p
  no mesmo hub USB 2.0 saturam a banda — a de rosto deve ficar em 640×480
  (é o padrão). O log também avisa sozinho quando a resolução pedida é
  ignorada pelo driver.
- **`detecção ... (pior NNN ms)` no STATS** — a média de `detecção` engana: o
  reconhecimento caro roda de vez em quando, então a média se dilui e o que o
  olho percebe é o **pior**. `pior` na casa de 70-250 ms é o ArcFace gerando o
  embedding; se aparecer a cada poucos segundos com a cena parada, algo está
  impedindo o app de reaproveitar o resultado anterior.
- **Pessoa errada no carimbo** — o app precisa de **duas ou mais** pessoas
  cadastradas para ter margem de decisão. Com uma só, qualquer rosto que chegue
  perto o bastante é aceito (não existe "rival" para comparar). Cadastre todos
  os operadores. O log traz `[Rank]`/`[Multi]` com o cosseno de cada caixa.
- **Foto não sai ao apertar o botão** — confira se a captura não está **Pausada**
  (LED permanece vermelho e o botão fica desabilitado) ou se ainda não passou o
  tempo mínimo entre fotos.
- **Logs** — consulte `%APPDATA%\ScreenLab\logs` para diagnosticar.

## Considerações de operação 24/7

- O sistema mantém a câmera aberta — não deixe o PC suspender
  (energia > suspensão desativada) se precisar de operação contínua.
- O app é instância única: rodar duas vezes apenas mostra a janela existente.
- A janela abre **visível** para os operadores. Clicar no **X** encerra o
  programa; **minimizar** manda para a bandeja, onde a captura continua (menu da
  bandeja → **Sair** também encerra).