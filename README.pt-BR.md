<p align="center">
  <img src="docs/logo.png" width="110" alt="Logo do Alien G-Helper">
</p>

<h1 align="center">Alien G-Helper</h1>

<p align="center">
  Central de controle leve, no estilo do G-Helper, para notebooks <b>Dell G15</b> (e outros Dell G / Alienware com a interface WMI do AWCC).<br>
  Fans, energia da CPU/GPU, limite de carga da bateria, iluminação do teclado e monitor de hardware — sem o Alienware Command Center.
</p>

<p align="center">
  <a href="README.md">🇺🇸 English</a> ·
  <a href="docs/BUILDING.pt-BR.md">Compilar</a> ·
  <a href="docs/HOW-IT-WORKS.md">Como funciona</a> ·
  <a href="CHANGELOG.md">Changelog</a>
</p>

---

<!-- Prints: salve as capturas como `docs/screenshots/main.png` e `fans.png` e depois tire os marcadores de comentário em volta das imagens abaixo.
<p align="center">
  <img src="docs/screenshots/main.png" width="300" alt="Janela principal">
  <img src="docs/screenshots/fans.png" width="520" alt="Fans and Power">
</p>
-->

## Recursos

| Área | O que tem |
|---|---|
| **Fan Control** | Silent, Balanced, Turbo (G-Mode) e Manual (curvas próprias de CPU e GPU). Editor com "Clamp to Grid", ponto ao vivo na curva, **calibração dos fans** (descobre a rotação máxima real de cada um) e fail-safe que liga o Turbo quando esquenta demais. |
| **Energia da CPU** | Presets de um clique **Eco (15 W)**, **Balanced (45 W)**, **Performance (90 W)** ou Custom com sliders de SPL / sPPT / fPPT, aplicados na SMU do Ryzen pelo driver PawnIO e reaplicados quando o firmware (AMD PMF) os desfaz. Modo de energia do Windows, CPU boost e "unlock clocks" na aba Advanced. |
| **GPU (NVIDIA)** | Offset de clock do núcleo e da memória, limite de clock e power target (NVAPI / `nvidia-smi`). |
| **Bateria** | Modos de carga da BIOS Dell: Standard, ExpressCharge, Adaptive, Primarily AC e **limite de carga Custom** (ex.: parar em 80 %). Fica gravado na BIOS e funciona com o app fechado. |
| **Iluminação do teclado** | AlienFX APIv4 por USB HID: cor por zona, Static / Pulse / Breath / Spectrum / Rainbow, brilho, gravação na memória do teclado (as cores ficam ao ligar o PC e com o app fechado) e restauração depois da suspensão. |
| **Monitor** | Temperatura, uso, clock e consumo de CPU e GPU, RAM/VRAM, RPM dos fans, gráfico de atividade (1/5/15 min) e contador de **FPS / frametime** para jogos DirectX 9–12 (ETW, como o PresentMon). |
| **Perfis por app** | Para cada jogo/app: modo térmico, curvas e preset de energia, trocados sozinhos enquanto ele roda. |
| **Bandeja** | Ícone com temperaturas, RPM e modo na dica, menu rápido de modos e a **tecla G** liga/desliga o Turbo de qualquer lugar. |
| **Visual** | Janela principal compacta de uma coluna como o G-Helper e janelas de detalhe (Fans and Power, Keyboard, Monitor, Extra). Mica ou Flat, tema claro/escuro/sistema e cor de destaque. |

## Requisitos

- Windows 10 (19041+) ou 11, 64 bits
- **.NET 8 Desktop Runtime** (dispensável na versão self-contained)
- Roda **como administrador** (o app pede sozinho)
- Opcional: driver **[PawnIO](https://github.com/namazso/PawnIO)** para os limites da CPU (`winget install namazso.PawnIO`) e driver NVIDIA para a GPU
- **Feche o Alienware Command Center e o Dell Power Manager**: eles brigam pelos fans, pela iluminação e pela bateria.

Testado no **Dell G15 5525** (Ryzen 7 6800H / RTX 3060 Laptop). Outros Dell G e Alienware com a interface WMI do AWCC devem funcionar para fans e modos; os limites da CPU precisam de um Ryzen suportado pelo módulo da SMU.

## Download

Baixe o `AlienGHelper.exe` em [Releases](../../releases) (ou no artefato da última execução do workflow **Build**), coloque numa pasta e abra.

Para abrir com o Windows: **Settings › Other › Run on Startup** (marque de novo se mudar o .exe de lugar).

## Compilar

```powershell
winget install Microsoft.DotNet.SDK.8
git clone https://github.com/<seu-usuario>/AlienGHelper.git
cd AlienGHelper
dotnet publish src/AlienGHelper/AlienGHelper.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
.\publish\AlienGHelper.exe
```

Passo a passo completo e solução de problemas: **[docs/BUILDING.pt-BR.md](docs/BUILDING.pt-BR.md)**.

## Onde ficam as coisas

| O quê | Onde |
|---|---|
| Configurações (curvas, cores, perfis, limites) | `%APPDATA%\TccG15\settings.json` |
| Logs de inicialização / erro | `%APPDATA%\TccG15\startup.log`, `crash.log` |
| Modo de carga da bateria | BIOS da Dell |
| Cores do teclado ("Save to keyboard memory") | controlador do teclado |
| Iniciar com o Windows | tarefa `TCC_G15` no Agendador de Tarefas |

## Avisos

- O app conversa direto com a BIOS, o controlador embarcado, a SMU da CPU e o driver da GPU, pelas mesmas interfaces das ferramentas da Dell e do G-Helper. O firmware mantém as proteções térmicas, mas **use por sua conta e risco**.
- Limites altos (Performance) só rendem até onde a refrigeração aguenta; a CPU reduz o clock sozinha no limite de temperatura.
- A calibração deixa os fans no máximo por 1–2 minutos.

## Créditos e licença

Baseado em [tcc-g15](https://github.com/AlexIII/tcc-g15) (AlexIII), [G-Helper](https://github.com/seerge/g-helper) (seerge), [alienfx-tools](https://github.com/T-Troll/alienfx-tools) (T-Troll), [PawnIO](https://github.com/namazso/PawnIO), [RyzenAdj](https://github.com/FlyGoat/RyzenAdj), NvAPIWrapper, TraceEvent e Avalonia UI. Detalhes em [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Licença **[GPL-3.0](LICENSE)**.

Projeto independente, **sem vínculo com Dell, Alienware, ASUS ou o projeto G-Helper**. As marcas citadas pertencem aos seus donos e aparecem só para indicar compatibilidade.
