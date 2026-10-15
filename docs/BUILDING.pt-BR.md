# Como compilar o Alien G-Helper

> 🇺🇸 English: [BUILDING.md](BUILDING.md)

Tutorial passo a passo para gerar o `AlienGHelper.exe` no Windows 10/11 (64 bits).

---

## 1. Instalar o .NET 8 SDK (uma vez só)

Abra o **Terminal** ou o **PowerShell** e rode:

```powershell
winget install Microsoft.DotNet.SDK.8
```

Sem winget: baixe o instalador "SDK 8.0 – Windows x64" em https://dotnet.microsoft.com/download/dotnet/8.0.

Feche e abra o terminal de novo e confira:

```powershell
dotnet --version
```

Tem que aparecer `8.0.xxx` (um número maior, como 9.0, também funciona).

> A primeira compilação baixa os pacotes do NuGet (Avalonia, NvAPIWrapper, TraceEvent…), então precisa de internet.

---

## 2. Baixar o código

Com Git:

```powershell
git clone https://github.com/<seu-usuario>/AlienGHelper.git
cd AlienGHelper\src\AlienGHelper
```

Sem Git: no GitHub, **Code › Download ZIP**, extraia e abra um terminal na pasta `src\AlienGHelper`
(a que tem o arquivo **`AlienGHelper.csproj`**). Dica: na barra de endereço do Explorer, digite `powershell` e Enter.

---

## 3. Testar rodando direto

```powershell
dotnet run -c Release
```

- O app exige **administrador** (para falar com a BIOS, os fans e o driver PawnIO). Para o `dotnet run`, abra o terminal com **"Executar como administrador"**; num terminal comum aparece o erro *"The requested operation requires elevation"* (740).
- Para fechar: **Quit** na janela ou no ícone da bandeja.

---

## 4. Gerar o executável para usar no dia a dia

### Opção A — um único .exe (recomendado)

Precisa do **.NET 8 Desktop Runtime** instalado no PC (quem tem o SDK do passo 1 já tem).

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

### Opção B — .exe que roda em qualquer PC, sem instalar o .NET

Fica maior (~80–100 MB), mas não depende de nada:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

O resultado fica em **`src\AlienGHelper\publish\AlienGHelper.exe`**.

Copie a pasta `publish` para um lugar fixo, por exemplo `C:\Program Files\Alien G-Helper\`, e abra o `AlienGHelper.exe` de lá.

---

## 5. Primeira execução

1. **Feche o Alienware Command Center (AWCC)** e, se tiver, o **Dell Power Manager**: eles brigam pelos fans, pela iluminação e pelo limite de bateria.
2. Abra o `AlienGHelper.exe` com dois cliques (o Windows pede permissão de administrador; aceite).
3. Em **Settings › Other**, marque **Run on Startup** para abrir junto com o Windows.
   - Sempre que mudar o .exe de lugar (ou depois de recompilar para outra pasta), **desmarque e marque de novo**: a tarefa aponta para o caminho do .exe.

### Recursos opcionais

| Recurso | Precisa de |
|---|---|
| Energia da CPU (Eco / Balanced / Performance / Custom) | Driver **PawnIO**: `winget install namazso.PawnIO` (reinicie o app depois) |
| Clocks da GPU, uso, VRAM | Driver NVIDIA instalado |
| Monitor de FPS | App rodando como administrador (já é o padrão) |
| Limite de carga da bateria | BIOS Dell com a interface de bateria (o G15 tem) |

---

## 6. Atualizar para uma versão nova

1. Feche o app (**Quit**).
2. Atualize o código: `git pull` (ou baixe o ZIP novo).
3. Rode de novo o comando do passo 4.
4. Copie o novo `publish\AlienGHelper.exe` por cima do antigo.

As configurações ficam em `%APPDATA%\TccG15\settings.json` e **não se perdem** ao atualizar.

---

## 7. Problemas comuns

| Sintoma | O que fazer |
|---|---|
| `dotnet` não é reconhecido | Feche e abra o terminal depois de instalar o SDK; ou reinicie o PC. |
| Erro de restauração do NuGet (`NU1101`, `NU1301`) | Verifique a internet e rode `dotnet nuget locals all --clear` e depois o comando de novo. |
| `error CS…` / `AVLN…` ao compilar | Copie a mensagem inteira (arquivo, linha e texto) e mande para correção. |
| "Another instance of this app is already running" | Feche o app pela bandeja ou finalize **AlienGHelper** no Gerenciador de Tarefas. A versão completa (TccG15) também conta como instância. |
| Só aparece o processo, sem janela | Veja `%APPDATA%\TccG15\startup.log` (última etapa) e `%APPDATA%\TccG15\crash.log`. |
| Fans não mudam / iluminação volta ao padrão | Feche o AWCC e o Dell Power Manager. |
| "PawnIO driver is not installed" | `winget install namazso.PawnIO` e reinicie o app. |

---

## Resumo rápido

```powershell
winget install Microsoft.DotNet.SDK.8          # uma vez
git clone https://github.com/<seu-usuario>/AlienGHelper.git
cd AlienGHelper\src\AlienGHelper
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
.\publish\AlienGHelper.exe
```
