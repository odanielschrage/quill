---
title: "Windows port — native C#/.NET sibling implementation"
date: 2026-07-29
status: done
progress: "completo — fases 0–7 concluídas, 95 testes passando, binário único verificado"
affects: "platform support — new windows/ implementation alongside Sources/quill"
---

## Objetivo

Fazer o quill funcionar no Windows preservando sua identidade: binário único, ícone
na bandeja, duas faixas separadas, transcrição on-device, nada sai da máquina — e
**o mesmo contrato de arquivos em disco**, para que hooks `on_stop` e ferramentas
downstream funcionem nas duas plataformas sem alteração.

## Decisões tomadas

| Decisão | Escolha | Motivo |
|---|---|---|
| Stack | **C# / .NET 8** (`net8.0-windows`) | NAudio resolve mic + loopback WASAPI (a parte mais difícil) com risco baixo; `NotifyIcon` dá o tray trivialmente; `PublishSingleFile` preserva o "binário único". Runtimes .NET 8 + WindowsDesktop já presentes na máquina alvo. |
| Motor ASR | **Whisper.net** (whisper.cpp / GGML) | Multilíngue, incluindo português — Parakeet v2 é só inglês. Já era o fallback planejado no README. |
| Layout | **Mesmo repo**, pasta `windows/` | Um contrato de formato compartilhado, duas implementações nativas. |
| Formato de áudio | **WAV mono 16 kHz 16-bit** | É o que o ASR consome; gravável em streaming; um WAV truncado se recupera reconstruindo o header — preserva a propriedade que motivou o CAF. |
| AEC | **Desligado**, fora do MVP | O padrão do macOS já é `mic_voice_processing: false` desde o rca-001. O Voice Capture DSP do Windows é igualmente frágil. |

## Superfície da portabilidade

Não é um port — é uma reimplementação da camada de plataforma. AppKit,
AVFoundation, Core Audio e Core ML não existem no Windows, e `FluidAudio` é
dependência Core ML pura.

| Arquivo macOS | Linhas | Destino no Windows |
|---|---|---|
| `Config.swift` | 87 | Tradução direta (~85% da lógica) |
| `RecordingSession.swift` | 78 | Tradução direta (~90%) |
| `Transcription/TranscriptionCoordinator.swift` | 275 | Tradução direta (~85%); `/bin/sh` → `cmd.exe /c` |
| `Transcription/TranscriptionEngine.swift` | 22 | Interface idêntica |
| `Doctor.swift` | 123 | Estrutura sobrevive, checks trocam por completo |
| `Quill.swift` | 185 | `AppController` sobrevive; `NSApplication` → `ApplicationContext` |
| `Audio/SystemAudioRecorder.swift` | 174 | **Reescrita — fica mais simples** (WASAPI loopback) |
| `Audio/MicRecorder.swift` | 233 | **Reescrita** (WASAPI capture) |
| `Transcription/ParakeetEngine.swift` | 103 | **Reescrita** (Core ML → GGML) |
| `UI/MenuBarController.swift` | 114 | **Reescrita** (`NSStatusItem` → `NotifyIcon`) |
| `Install.swift` | 139 | **Reescrita** (LaunchAgent → registro `Run`) |
| `Notify.swift` | 15 | **Reescrita** (`osascript` → balloon tip) |
| `Info.plist` | — | **Desaparece** (Windows não tem TCC para loopback) |

~460 linhas de orquestração sobrevivem conceitualmente; ~760 linhas de código de
plataforma são reescrita — e a reescrita cobre todas as partes difíceis.

## Riscos identificados

### R1 — Loopback não entrega buffers em silêncio (crítico)

O WASAPI loopback **não dispara `DataAvailable` quando nada está tocando**. O
process tap do macOS entrega buffers contínuos, então `firstBufferAt` + arquivo
monotônico basta para alinhar as faixas.

No Windows, todo o silêncio colapsa: a faixa `system` fica **comprimida no tempo**,
seus timestamps deixam de bater com os do mic, e o `merged.sort` do coordinator
intercala falas erradas. Isso destrói exatamente a diarização de duas partes que é
a premissa do projeto.

**Mitigação (obrigatória no MVP):** manter um livro-caixa de amostras. A cada
callback, calcular a posição esperada a partir do tempo de parede decorrido desde
`firstBufferAt`; se a contagem escrita estiver atrasada além de um limite (~50 ms),
escrever zeros para fechar o buraco antes de escrever o buffer. Alternativa mais
grosseira: tocar silêncio no dispositivo durante toda a gravação — rejeitada por
manter o device ativo sem necessidade.

**Teste de aceitação:** gravar 60 s com áudio tocando só nos segundos 0–5 e 50–55;
o WAV resultante deve ter ~60 s, não ~10 s.

**✅ Resolvido e verificado (Fase 2).** `TrackWriter` implementa o livro-caixa com
tolerância de 250 ms e relógio monotônico (`Stopwatch`, não tempo de parede — uma
correção NTP ou virada de horário de verão no meio da reunião corromperia o
ledger). Verificado em duas frentes:

- determinístico, com relógio injetado: o cenário 0–5 s / 50–55 s produz 60,0 s
  exatos com 50 s de silêncio inserido
- hardware real, via `quill gaptest`: captura de 12 s com tom apenas nos segundos
  0–3 e 7–10 rendeu **12,36 s com 6,09 s inseridos** — os dois trechos ociosos
  (4 s + 2 s) reconstruídos

Confirmado empiricamente que o loopback realmente não entrega nada em silêncio:
uma captura com nada tocando produziu um arquivo de 0 byte.

### R2 — Desempenho de transcrição nesta máquina

O alvo é um i7-7500U (2 núcleos, 2017), sem GPU CUDA. O Neural Engine do Apple
Silicon faz Parakeet a ~180x tempo real (1 h em ~20 s). Em CPU dessa classe, espere
**1–5x tempo real** — uma reunião de 1 hora leva minutos. A promessa de "20 segundos
por hora" do README não se transfere e a documentação Windows não deve repeti-la.

**Ação:** benchmark explícito (Fase 3) de `tiny`/`base`/`small`/`medium` quantizados
para escolher o default por medição, não por suposição. Saída pela iGPU Intel
(OpenVINO/DirectML) fica como investigação de Fase 7.

### R3 — Troca de dispositivo no meio da sessão

Usuários Windows trocam headset com frequência, e o NAudio sinaliza invalidação do
device. A versão macOS também não trata isso, mas no Windows é comum o bastante para
merecer tratamento. Fora do MVP; Fase 7.

### R4 — Captura por processo não existe neste Windows

Loopback por processo exige Windows 10 build 20348+ / Windows 11. O alvo é build
19045, então só o loopback global está disponível. Isso **coincide com o
comportamento atual do macOS** (tap global), incluindo o mesmo gotcha: grava
notificações, música, tudo. Sem impacto na paridade.

## Plano

### Fase 0 — Ambiente ✅ concluída

A máquina tem só os runtimes .NET, não o SDK.

```bash
winget install --id Microsoft.DotNet.SDK.8 --source winget
```

### Fase 1 — Esqueleto e contrato de arquivos ✅ concluída

Portar primeiro tudo que é lógica pura — testável sem tocar em áudio.

- `windows/Quill.Win/Quill.Win.csproj`: `net8.0-windows`, `UseWindowsForms=true`,
  `OutputType=WinExe`, `AssemblyName=quill`.
- Traduzir `Config`, `RecordingSession`, `SessionMeta`, `Transcript`,
  `TranscriptionCoordinator`, `ITranscriptionEngine`.
- **Contrato de formato — idêntico, sem exceção:**
  - pasta `yyyy.MM.dd-HHmm` com sufixo `-2`, `-3`… em colisão, `CultureInfo.InvariantCulture`
  - `meta.json`: `started`, `ended`, `duration_seconds`, `files{mic,system}`,
    `start_offset_ms{mic,system}` — ISO 8601, chaves ordenadas, indentado
  - `transcript.json`: `engine`, `model`, `created_at`, `segments[{speaker,start_ms,end_ms,text}]`
  - `transcript.md`: `# <título>`, linha `engine: …`, e `**[m:ss] falante:** texto`
  - `transcribe.log`: append de `<ISO8601> <mensagem>`
  - escrita atômica de ambos os transcripts (temp + rename)
- Config em `%APPDATA%\quill\config.json`, com fallback de leitura em
  `~/.config/quill/config.json` para quem sincroniza dotfiles.
- Raiz default: `%USERPROFILE%\Recordings`.
- `on_stop` → `cmd.exe /c "<cmd> "<dir>""`, com atenção ao aninhamento de quotes.
- A fila continua sendo o filesystem: `meta.json` presente + `transcript.json`
  ausente = pendente. `resumePending` traduz literalmente.

**Novo em relação ao macOS:** `transcription.language` (`auto` | `pt` | `en` | …).
Whisper é multilíngue; o Parakeet não precisava dessa chave. Documentar como
extensão específica do Windows.

### Fase 2 — Captura de áudio ✅ concluída

Uma descoberta não prevista no plano, encontrada ao rodar em hardware real:
`FirstBufferAt` era exposto como `_writer?.FirstBufferAt`, e `Stop()` anula o
writer — mas `RecordingSession.Stop()` para as duas faixas **e só depois** lê o
skew para escrever `start_offset_ms`. Os dois offsets colapsavam para zero
silenciosamente, exatamente o mesmo dano ao alinhamento que o R1 causaria. O
valor agora é latcheado antes do dispose, e o contrato ficou explícito em
`IAudioRecorder`. Bug encontrado só porque o `meta.json` de uma captura real foi
inspecionado — os testes com fakes passavam, porque o fake retinha o valor.

Escopo original:

- `SystemAudioRecorder`: `WasapiLoopbackCapture` (ou o `WasapiRecorder` do NAudio 3
  com `WithLoopbackCapture()`) no device de render default. Reamostrar do mix format
  (tipicamente 48 kHz estéreo float) para mono 16 kHz no caminho de escrita.
- **Implementar a mitigação de R1 aqui.** É o item de maior valor da fase.
- `MicRecorder`: `WasapiCapture` no device de captura default, downmix para mono,
  reamostragem para 16 kHz.
- Preservar a lição do rca-001: *liveness check* de 1 segundo — se o pico
  permanecer em zero digital, avisar em stderr/log. Sem o fallback de reconstrução
  de grafo, que era específico do `VoiceProcessingIO`.
- `WaveFileWriter` com `Flush()` periódico (~5 s) para que um crash deixe um arquivo
  recuperável. Documentar a recuperação: WAV truncado → reconstruir o header RIFF.
- Manter `firstBufferAt` por faixa e a semântica de `start_offset_ms` intactas.
- Manter a regra do `RecordingSession.start()`: se o mic falhar depois do loopback
  ter iniciado, derrubar o loopback — nunca meia sessão gravando em silêncio.

### Fase 3 — Transcrição ✅ concluída

**R2 quantificado.** Benchmark em 75 s de pt-BR sintetizado, no i7-7500U: `base`
1,34 xRT / 5,1 % WER, `small` 0,42 xRT / 2,2 % WER, `medium` 0,16 / 1,4 %,
`large-v3-turbo` 0,11 / 1,4 %. Default fixado em `small`; `base` domina `tiny`
(mesma velocidade, metade do erro) e é a recomendação para CPU fraca. Tabela
completa e ressalvas em `windows/README.md`.

**Defeito encontrado e contornado:** o `WhisperGgmlDownloader` do Whisper.net não
tem timeout — uma conexão estagnada ficou **14 horas** pendurada sem transferir um
byte nem reportar nada. Inaceitável para um daemon que baixa em background depois
de uma reunião. O download passou a ser feito com `HttpClient` próprio: timeout de
estagnação de 60 s re-armado a cada leitura, retomada por `Range` entre tentativas,
verificação contra `Content-Length` e progresso em porcentagem. O Whisper.net segue
responsável só pela inferência.

**Oportunidade anotada para a Fase 7:** a faixa `system` carrega o silêncio
inserido pelo livro-caixa do R1, e o Whisper paga compute para transcrevê-lo. Numa
reunião em que só um lado fala, isso pode ser a maior parte da faixa. O Whisper.net
expõe `GetGgmlSileroVadModelAsync` — VAD antes da inferência atacaria exatamente
esse desperdício, e é provavelmente o maior ganho de desempenho disponível.

Escopo original:

- `Whisper.net` + `Whisper.net.Runtime`. Modelo baixado uma vez para
  `%LOCALAPPDATA%\quill\models\`.
- `WhisperEngine : ITranscriptionEngine`, mesmo ciclo `prepare` / `transcribe` /
  `release` — o coordinator continua liberando o modelo quando a fila drena.
- O Whisper já entrega segmentos com `Start`/`End`, então a lógica de agrupamento de
  `WordTiming` do `ParakeetEngine` (quebra por pontuação, gap de 1 s, teto de 60
  palavras) **não é necessária**. O deslocamento por `offset` e o merge por
  timestamp continuam.
- Manter a checagem de arquivo com zero frames antes de transcrever. No .NET a
  exceção seria capturável — mas a checagem é barata e mantém paridade de comportamento.
- **Benchmark (entregável desta fase):** medir fator de tempo real de
  `tiny`/`base`/`small`/`medium` quantizados nesta CPU, com áudio real em português,
  e fixar o default a partir do resultado. Registrar os números no README Windows.

### Fase 4 — Tray, notificações e ciclo de vida ✅ concluída

Dois desvios do plano, ambos justificados:

- **O `AttachConsole` subiu da Fase 5 para cá.** Trocar `OutputType` para `WinExe`
  cega imediatamente todos os harnesses de dev, então a ponte de console tinha que
  vir junto — não é escopo extra, é consequência direta da mudança.
- **`LibraryImport` foi trocado por `DllImport`.** O gerador exige
  `AllowUnsafeBlocks` no projeto inteiro, o que é uma concessão grande demais por
  um único P/Invoke fora de caminho quente.

Uma diferença de plataforma que o plano não previa: no macOS as *template images*
adaptam o ícone ao tema da barra sozinhas. No Windows não existe equivalente, e um
ícone branco some numa bandeja clara. `FeatherIcon` lê
`Themes\Personalize\SystemUsesLightTheme` e escolhe a cor.

Verificado: daemon sobe, sobrevive, e retomou sozinho 2 sessões pendentes na
primeira execução real.

Escopo original:

- `ApplicationContext` sem form + `NotifyIcon` + `ContextMenuStrip`. Itens espelhando
  o macOS: label de estado (desabilitado), label de transcrição (oculto quando nulo),
  separador, Start/Stop recording, Open recordings folder, Quit.
- Estado de gravação: dois `.ico` embutidos como recurso (pena normal / pena
  vermelha), gerados uma vez a partir do mesmo path Lucide do
  `MenuBarController.featherSVG`. Evita dependência de rasterizador SVG e mantém o
  single-file honesto.
- A bandeja do Windows não tem texto inline como a barra de menus do macOS: o
  contador decorrido vai no `NotifyIcon.Text` (tooltip) **e** no label do menu.
- Notificações via `NotifyIcon.ShowBalloonTip` — zero dependências, é o análogo
  honesto do `osascript display notification`. Toast do WinRT exigiria AUMID e atalho
  no Start Menu, ou seja, o bundle que o projeto recusa.
- **`SystemEvents.SessionEnding` é obrigatório.** A versão macOS trata SIGINT para
  finalizar arquivos; no Windows o equivalente é logoff/shutdown. Sem isso, desligar
  o PC durante uma reunião deixa WAVs sem header e sem `meta.json`.

### Fase 5 — Doctor e autostart ✅ concluída

**Desvio do plano: a CLI é escrita à mão, não com `System.CommandLine`.** Verifiquei
no NuGet antes de decidir — a última versão publicada é `3.0.0-preview.6`, ou seja,
não há release estável. Três comandos e três flags não justificam uma dependência
preview num projeto cuja forma inteira é "um binário, poucas dependências". No
macOS o ArgumentParser é estável e first-party; aqui o equivalente não existe.

Descoberta ao inspecionar o registro real: o consentimento de microfone tem **três
portas** independentes, e qualquer uma nega. A que as pessoas esquecem é *"Let
desktop apps access your microphone"*, guardada separadamente em
`ConsentStore\microphone\NonPackaged` — quill não é app empacotado. Há ainda uma
entrada por executável, com `\` substituído por `#` no caminho. As três são checadas.

O `install` ganhou um aviso que o plano não previa: registrar um caminho dentro de
`bin\Debug` ou `bin\Release` cria uma entrada que para de funcionar silenciosamente
no próximo clean. Verificado com ciclo completo install → conferir registro →
uninstall → conferir limpo.

Escopo original:

- Checks do `doctor`, reescritos para o que o Windows realmente pode verificar:
  - permissão de microfone: `HKCU\...\CapabilityAccessManager\ConsentStore\microphone`
  - existe device de captura default
  - existe device de render default (o loopback depende de um)
  - pasta de gravações gravável
  - modelo Whisper em cache, e espaço em disco
  - **remover** o check de "system audio — estado incognoscível": no Windows o
    loopback não pede permissão. Esse item some, e é uma melhoria real.
- Autostart: escrever `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Um único
  write, sem elevação, e com o mesmo escopo de usuário do LaunchAgent. CLI idêntica:
  `quill install --launch-at-login` / `--uninstall`.
- CLI com `System.CommandLine` espelhando o ArgumentParser: `run --out`, `doctor`,
  `install`.
- **Gotcha:** um `WinExe` não tem console, então `quill doctor` não imprimiria nada
  no terminal. Chamar `AttachConsole(ATTACH_PARENT_PROCESS)` nos subcomandos de
  console.

### Fase 6 — Empacotamento e documentação ✅ concluída

**O risco previsto para esta fase se materializou, e a flag óbvia era a errada.**
`IncludeNativeLibrariesForSelfExtract=true` empacota e extrai as bibliotecas
nativas, mas deixa `AppContext.BaseDirectory` apontando para a pasta do `.exe`. O
Whisper.net procura `whisper.dll` relativo a isso, olha no lugar errado, e toda
transcrição morre com `Native Library not found in default paths`.

A flag correta é `IncludeAllContentForSelfExtract=true`, que extrai tudo para um
diretório temporário **e** aponta o `BaseDirectory` para lá. Custo: primeira
inicialização mais lenta.

O que torna isso perigoso é *quando* falha: `doctor` passa, gravação funciona, e o
erro só aparece no `transcribe.log` depois de uma reunião. Qualquer mudança nas
opções de publish exige rodar uma transcrição real a partir do binário publicado.

**Falha secundária exposta pelo mesmo erro:** o harness `transcribe` sondava o
disco esperando `transcript.md`, que num job que falha nunca aparece — transformou
um erro claro no log numa espera de uma hora. Passou a aguardar o sinal da própria
fila (`Idle` ou `Failed`), com `ManualResetEventSlim` para que um drain que termine
antes da espera não vire corrida.

Escopo original:

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

- Resultado: `quill.exe` (~80 MB; as DLLs nativas do Whisper.net são extraídas para
  temp no primeiro run — documentar).
- README: matriz de plataformas, e **não** repetir os números de desempenho do Apple
  Silicon na seção Windows.

### Fase 7 — Pós-MVP

- ✅ **VAD antes da inferência** — feito. Silero via Whisper.net, ligado por padrão
  (`transcription.vad`).

  O ganho de velocidade é real mas modesto (1,30× num teste com 38 % de silêncio;
  escala com quanto silêncio a faixa tem). **O ganho maior foi outro:** alimentado
  com 30 s de silêncio digital, o Whisper alucina — a execução sem VAD abre com um
  segmento `[Música]` em 0:00, conteúdo inventado que numa sessão real seria
  atribuído a um falante. Com VAD esse segmento não existe.

  O Whisper.net expõe VAD como detector **separado**, não integrado ao processador,
  então o remapeamento de timestamps é responsabilidade nossa — exatamente o risco
  a vigiar. Cada região é deslocada de volta pelo seu ponto de início; `quill
  vadtest` afere o primeiro segmento contra um lead de 30 s injetado (deu 29,9 s).

  Duas salvaguardas: nunca devolve transcript vazio (sem fala detectada numa faixa
  com áudio → transcreve inteira), e regiões separadas por menos de 2 s são
  fundidas, porque pausas de frase fragmentaram uma fala contínua em 11 regiões e
  cada região custa uma chamada de inferência.
- ✅ **Resiliência a troca de dispositivo (R3)** — feito.

  São dois modos de falha distintos, e o segundo é o perigoso. O dispositivo pode
  ser **invalidado** (desplugar um headset USB), o que aparece via
  `RecordingStopped` e ao menos é detectável. Ou o **padrão muda com o dispositivo
  antigo continuando válido**: pluga-se um fone e o loopback segue gravando
  obedientemente os alto-falantes, agora mudos. Nada dá erro — a faixa
  simplesmente emudece pelo resto da reunião.

  Os dois são tratados reabrindo a captura no padrão atual; o segundo via
  `IMMNotificationClient` em `OnDefaultDeviceChanged`, filtrado pelo lado do grafo
  desta faixa **e** pelo papel `Console`, porque o Windows dispara a notificação
  uma vez por papel e agir nas três reabriria três vezes por um único plug.

  O ponto de projeto: o `TrackWriter` sobrevive à captura — mesmo arquivo, mesmo
  livro-caixa, mesmo `FirstBufferAt`. Assim a troca vira um buraco que o ledger do
  R1 preenche, em vez do fim da faixa. `quill devicetest` força duas reaberturas e
  confere: 12,51 s de duração, 2 reaberturas, **1,63 s de padding** (os buracos), e
  áudio ainda chegando no fim.

  Falhando cinco vezes seguidas, desiste com notificação — mas a sessão continua e
  a faixa mantém o comprimento, porque o `Stop()` preenche até o tempo decorrido.
  Um dispositivo morto custa o áudio daquela faixa, nunca o alinhamento da outra.
- ✅ **Supressão de eco no nível do transcript** — feito, e escolhido em vez do
  Voice Capture DSP. O quill já tem a faixa distante limpa e as duas faixas num só
  relógio, então o lugar mais barato e confiável de remover eco é o transcript, não
  o áudio. É a ideia do rca-001, e nada nela é específico do Windows — a mesma regra
  portaria para o build Swift sem mudança.

  Um desvio do esboço do rca-001: a métrica é **contenção**, não similaridade fuzzy.
  Eco costuma ser uma captação degradada e parcial do lado distante, então
  similaridade simétrica lê baixo exatamente quando a confiança deveria ser alta.
  Contenção — que fração das palavras do mic já estava tocando naquele instante —
  não tem esse problema, e falha para o lado seguro no caso que importa: em
  double-talk o mic contribui palavras que o sistema não tem, a contenção cai, e o
  segmento sobrevive.

  Conservador de propósito: segmentos com menos de 3 tokens nunca são tocados, e é
  exigida sobreposição temporal (o que separa eco de citar alguém um minuto depois).
  **Toda remoção vai para o `transcribe.log`** com texto e score — é heurística, então
  o que ela descarta continua recuperável.

- AEC no caminho do áudio via Voice Capture DSP: não implementado, e provavelmente
  desnecessário agora. Fica como opção se a supressão no transcript se mostrar
  insuficiente em uso real.

- **Diarização de múltiplos participantes: avaliada e adiada (2026-08-01).**

  Com 3+ pessoas, todos do lado remoto saem como um único `them`. É estrutural: a
  plataforma da reunião mistura os participantes num só fluxo antes de chegar ao
  dispositivo de áudio. Captura por processo também não resolveria — daria "o
  navegador", não "Maria".

  Três caminhos foram considerados:

  1. **Atribuição por contexto**, via `on_stop` — custo zero de código e entrega
     **nomes reais**, porque as pessoas se chamam pelo nome em reunião. Rompe o
     "nada sai da máquina" se usar API externa.
  2. **Diarização local** com sherpa-onnx (`pyannote-segmentation-3.0` + CAM++,
     ambos ONNX, API C# disponível). Fica no `system.wav` e rotula `them-1`,
     `them-2`. Custos: ~35 MB de modelos, outro passe de CPU numa máquina que já é
     o gargalo, clusters **anônimos**, e degradação severa com fala sobreposta e
     turnos curtos — exatamente o que reuniões têm. O campo `speaker` passaria a
     divergir do macOS.
  3. **Documentar e não mudar** — escolhido.

  **Motivo da escolha:** a diarização de duas partes ainda não foi validada uma
  única vez numa call real. Construir clustering sobre uma base não verificada é
  construir na areia, e a call real ainda pode mostrar que o caminho 1 basta.
  Reavaliar depois de uso real acumulado.
- Aceleração por iGPU (OpenVINO/DirectML).
- Motor Parakeet via `org.k2fsa.sherpa.onnx` para paridade em inglês — requer spike
  antes: os docs C# não confirmam timestamps por palavra em modelos NeMo transducer.

## Ordem de execução

Fase 0 → 1 → 2 → 3 → 4 → 5 → 6. As fases 2 e 3 são independentes depois da 1 e
podem ser paralelizadas. A Fase 2 carrega praticamente todo o risco técnico do
projeto (R1), então vale atacá-la antes da 3.

## Critério de pronto (MVP)

1. `quill.exe` sobe na bandeja, grava mic + áudio do sistema em duas faixas.
2. R1 verificado pelo teste de aceitação: silêncio preservado no timeline.
3. Transcrição automática em português com falantes `me`/`them` corretamente
   intercalados.
4. Uma sessão gravada no Windows e uma no macOS produzem `meta.json` e
   `transcript.json` estruturalmente idênticos.
5. Matar o processo no meio da gravação deixa arquivos recuperáveis; o job pendente
   é retomado no próximo launch.
6. Desligar o Windows durante a gravação finaliza a sessão limpa.
